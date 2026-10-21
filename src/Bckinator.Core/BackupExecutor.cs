using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bckinator.Core;

public sealed class BackupExecutor(BackupScanner scanner)
{
    public async Task<BackupRun> ExecuteAsync(BackupProfile profile, AppSettings settings, string runsDirectory, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        // Take a snapshot so edits cannot change a running job.
        profile = ProfileStore.Clone(profile);
        var run = new BackupRun { ProfileId = profile.Id, ProfileName = profile.Name };
        var timer = Stopwatch.StartNew();
        string staging = "", temporaryArchive = "", destination = "";
        var logGate = new object();
        StreamWriter? writer = null;
        bool finalized = false;
        long lastProgressMs = -100;
        void Report(string operation, double? percent = null, bool log = true)
        {
            if (!log && timer.ElapsedMilliseconds - lastProgressMs < 100) return;
            lastProgressMs = timer.ElapsedMilliseconds;
            if (log)
            {
                lock (logGate) { writer?.WriteLine($"{DateTimeOffset.Now:O} {operation}"); }
            }
            progress?.Report(new(operation, percent, log ? operation : null));
        }

        try
        {
            Directory.CreateDirectory(runsDirectory);
            run.LogPath = Path.Combine(runsDirectory, run.Id.ToString("N") + ".log");
            writer = new StreamWriter(new FileStream(run.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Report($"Starting {profile.Name}");
            var paths = ProfileValidator.ValidatePaths(profile);
            destination = paths.Destination;
            string name = BackupNaming.Generate(profile.NamingPattern, run.StartedAt);
            string extension = profile.Format switch { OutputFormat.SevenZip => ".7z", OutputFormat.WinRar => ".rar", _ => "" };
            run.OutputPath = Path.Combine(destination, name + extension);
            string sidecar = run.OutputPath + ".manifest.json";
            if (File.Exists(run.OutputPath) || Directory.Exists(run.OutputPath) || File.Exists(sidecar) || Directory.Exists(sidecar))
                throw new IOException($"The output name already exists: {run.OutputPath}. Change the naming pattern or wait for the next timestamp.");
            if (profile.Format == OutputFormat.SevenZip && !File.Exists(settings.SevenZipPath))
                throw new FileNotFoundException("Set a valid 7z.exe path in Settings before starting.", settings.SevenZipPath);
            if (profile.Format == OutputFormat.WinRar && !File.Exists(settings.RarPath))
                throw new FileNotFoundException("Set a valid WinRAR Rar.exe path in Settings before starting.", settings.RarPath);

            var plan = await scanner.ScanAsync(profile, progress, ct);
            ct.ThrowIfCancellationRequested();
            if (plan.FileCount == 0) throw new IOException("No files match this profile. Inspect Preview before running the backup.");
            var warnings = plan.Entries.Where(e => e.IsWarning).Select(e => $"{e.RelativePath}: {e.Reason}").ToList();
            foreach (string warning in warnings) Report("Warning: " + warning);
            Directory.CreateDirectory(destination);
            CheckFreeSpace(destination, plan.TotalBytes, profile.Format);
            var manifest = new List<ManifestFile>();
            string stagedRoot = "";
            if (profile.Format != OutputFormat.Folder)
            {
                staging = Path.Combine(destination, ".bckinator-work-" + run.Id.ToString("N"));
                Directory.CreateDirectory(staging);
                temporaryArchive = Path.Combine(destination, $".{name}.{run.Id:N}.partial{extension}");
                ArchiveTool archive = profile.Format == OutputFormat.WinRar ? new WinRar(settings.RarPath) : new SevenZip(settings.SevenZipPath);
                manifest = await new DirectArchiveBackup(archive).CreateAsync(
                    profile, plan, paths.Source, staging, name, temporaryArchive, warnings, line => Report(line), ct);
            }
            else
            {
                staging = Path.Combine(destination, ".bckinator-staging-" + run.Id.ToString("N"));
                if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("The staging location already exists.");
                stagedRoot = Path.Combine(staging, name);
                Directory.CreateDirectory(stagedRoot);
                Report($"Staging {plan.FileCount:N0} files ({plan.TotalBytes:N0} bytes) in {staging}");
                long processed = 0;
                foreach (var entry in plan.Entries.Where(e => e.Included))
                {
                    ct.ThrowIfCancellationRequested();
                    string input = Path.GetFullPath(Path.Combine(paths.Source, entry.RelativePath));
                    string output = Path.GetFullPath(Path.Combine(stagedRoot, entry.RelativePath));
                    if (!ProfileValidator.IsSameOrBelow(input, paths.Source) || !ProfileValidator.IsSameOrBelow(output, stagedRoot))
                        throw new IOException("A selected path escaped the backup boundary.");
                    try
                    {
                        ValidateSourcePath(input, paths.Source, profile.ExcludeLinks);
                        if (entry.IsDirectory) { Directory.CreateDirectory(output); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        Report($"Copying {entry.RelativePath}", plan.TotalBytes == 0 ? 0 : processed * 100.0 / plan.TotalBytes, log: false);
                        var info = new FileInfo(input);
                        if (info.Length != entry.Size || info.LastWriteTimeUtc != entry.LastWriteUtc) throw new IOException("The source changed after scanning; run the backup again.");
                        string checksum;
                        await using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            byte[] buffer = new byte[131072];
                            long copied = 0;
                            int count;
                            while ((count = await source.ReadAsync(buffer, ct)) > 0)
                            {
                                await target.WriteAsync(buffer.AsMemory(0, count), ct);
                                hash.AppendData(buffer, 0, count);
                                copied += count;
                                Report($"Copying {entry.RelativePath}", plan.TotalBytes == 0 ? 0 : Math.Min(100, (processed + copied) * 100.0 / plan.TotalBytes), log: false);
                            }
                            await target.FlushAsync(ct);
                            if (copied != entry.Size || File.GetLastWriteTimeUtc(input) != entry.LastWriteUtc) throw new IOException("The source changed during copying.");
                            checksum = Convert.ToHexString(hash.GetHashAndReset());
                        }
                        File.SetLastWriteTimeUtc(output, entry.LastWriteUtc);
                        manifest.Add(new(entry.RelativePath, entry.Size, entry.LastWriteUtc, checksum));
                        processed += entry.Size;
                    }
                    catch (Exception ex) when (BackupScanner.IsFileError(ex) && profile.ContinueOnErrors)
                    {
                        // Retain partial data outside the archive root until the run succeeds.
                        if (!entry.IsDirectory && File.Exists(output)) File.Move(output, Path.Combine(staging, "incomplete-" + Guid.NewGuid().ToString("N")));
                        string warning = $"{entry.RelativePath}: {ex.Message}";
                        warnings.Add(warning);
                        Report("Warning: " + warning);
                    }
                }
                if (manifest.Count == 0) throw new IOException("No files could be copied successfully.");
                Report("Verifying copied files with SHA-256…");
                foreach (var file in manifest)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var stagedFile = File.OpenRead(Path.Combine(stagedRoot, file.RelativePath));
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(stagedFile, ct));
                    if (!actual.Equals(file.Sha256, StringComparison.Ordinal)) throw new IOException($"Checksum verification failed: {file.RelativePath}");
                }
                foreach (var directory in plan.Entries.Where(e => e.Included && e.IsDirectory).OrderByDescending(e => e.RelativePath.Length))
                    if (Directory.Exists(Path.Combine(stagedRoot, directory.RelativePath)))
                        Directory.SetLastWriteTimeUtc(Path.Combine(stagedRoot, directory.RelativePath), directory.LastWriteUtc);
            }

            string manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BackupManifest(1, run.Id, profile.Name, run.StartedAt, manifest, warnings), ProfileStore.JsonOptions), ct);
            if (profile.Format != OutputFormat.Folder)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryArchive, run.OutputPath, overwrite: false);
                run.OutputBytes = new FileInfo(run.OutputPath).Length;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                Directory.Move(stagedRoot, run.OutputPath);
                run.OutputBytes = manifest.Sum(f => f.Size);
            }
            finalized = true;
            run.FilesCopied = manifest.Count;
            // Publication is committed: cancellation from this point does not mislabel a verified backup.
            try { File.Move(manifestPath, sidecar, overwrite: false); }
            catch (IOException ex) { warnings.Add($"Backup verified, but the manifest remains in the run work directory: {ex.Message}"); }
            run.WarningCount = warnings.Count;
            run.Status = warnings.Count == 0 ? RunStatus.Completed : RunStatus.CompletedWithWarnings;
            run.Message = $"Verified {manifest.Count:N0} files; {warnings.Count} warnings.";
            if (File.Exists(manifestPath)) run.RetainedStagingPath = staging;
            else
            {
                // Source aliases have been detached. Cleanup refuses to follow any remaining links.
                string absolute = Path.GetFullPath(staging);
                string expectedDirectory = (profile.Format != OutputFormat.Folder ? ".bckinator-work-" : ".bckinator-staging-") + run.Id.ToString("N");
                if (!ProfileValidator.IsSameOrBelow(absolute, destination) || Path.GetFileName(absolute) != expectedDirectory)
                    throw new IOException("Staging cleanup boundary check failed.");
                try { DeleteWorkDirectory(absolute); }
                catch (Exception ex) when (BackupScanner.IsFileError(ex)) { run.RetainedStagingPath = staging; run.WarningCount++; run.Status = RunStatus.CompletedWithWarnings; Report("Cleanup warning: " + ex.Message); }
            }
            Report($"{run.Status}: {run.OutputPath}", 100);
        }
        catch (Exception ex)
        {
            run.Status = finalized ? RunStatus.CompletedWithWarnings : ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed;
            if (finalized) run.WarningCount++;
            run.Message = ex is OperationCanceledException ? "Backup cancelled. Partial output and run work files were retained." : ex.Message;
            if (Directory.Exists(staging)) run.RetainedStagingPath = staging;
            if (File.Exists(temporaryArchive)) run.Message += $" Partial archive: {temporaryArchive}";
            Report($"{run.Status}: {run.Message}");
        }
        finally
        {
            run.Duration = timer.Elapsed;
            writer?.Dispose();
        }
        return run;
    }

    internal static void ValidateSourcePath(string input, string source, bool excludeLinks)
    {
        string resolved = BackupScanner.ResolvePhysicalPath(input);
        if (!ProfileValidator.IsSameOrBelow(resolved, source)) throw new IOException("The source link target is outside the source directory.");
        if (excludeLinks)
        {
            string relative = Path.GetRelativePath(source, input);
            string current = source;
            foreach (string part in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A symbolic link or junction appeared after scanning.");
            }
        }
    }

    private static void CheckFreeSpace(string destination, long bytes, OutputFormat format)
    {
        string? root = Path.GetPathRoot(destination);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
        var drive = new DriveInfo(root);
        long required = checked(bytes + (format != OutputFormat.Folder ? bytes / 100 + 64L * 1024 * 1024 : 16L * 1024 * 1024));
        if (drive.AvailableFreeSpace < required) throw new IOException($"Insufficient free space. Allow at least {required:N0} bytes for output and overhead.");
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing to clean a linked work directory.");
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing to follow a link during work directory cleanup.");
            if (item is DirectoryInfo child) DeleteWorkDirectory(child.FullName);
            else item.Delete();
        }
        Directory.Delete(directory, recursive: false);
    }
}
