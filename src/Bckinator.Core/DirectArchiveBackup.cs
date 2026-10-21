using System.Security.Cryptography;
using System.Text;

namespace Bckinator.Core;

internal sealed class DirectArchiveBackup(ArchiveTool archive, bool allowAlias = true)
{
    public async Task<List<ManifestFile>> CreateAsync(BackupProfile profile, BackupPlan plan, string source, string work, string rootName, string archivePath, List<string> warnings, Action<string> report, CancellationToken ct)
    {
        var manifest = new List<ManifestFile>();
        report("Preflight: validating and hashing source files (no data copies)…");
        foreach (var entry in plan.Entries.Where(e => e.Included && !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string input = System.IO.Path.GetFullPath(System.IO.Path.Combine(source, entry.RelativePath));
                BackupExecutor.ValidateSourcePath(input, source, profile.ExcludeLinks);
                CheckMetadata(input, entry.Size, entry.LastWriteUtc);
                await using var file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                CheckMetadata(input, entry.Size, entry.LastWriteUtc);
                manifest.Add(new(entry.RelativePath, entry.Size, entry.LastWriteUtc, hash));
            }
            catch (Exception ex) when (BackupScanner.IsFileError(ex) && profile.ContinueOnErrors)
            {
                string warning = $"{entry.RelativePath}: {ex.Message}";
                warnings.Add(warning); report("Warning: " + warning);
            }
        }
        if (manifest.Count == 0) throw new IOException("No files could be read successfully for direct compression.");

        // Store directory entries only where they contain no selected files. Explicitly exclude
        // their children, including children created after scanning, to prevent recursive leaks.
        static IEnumerable<string> Parents(string relative)
        {
            string path = Glob.Normalize(relative);
            int slash;
            while ((slash = path.LastIndexOf('/')) >= 0) { path = path[..slash]; yield return path; }
        }
        var parentsWithFiles = manifest.SelectMany(f => Parents(f.RelativePath)).ToHashSet(StringComparer.Ordinal);
        var directories = plan.Entries.Where(e => e.Included && e.IsDirectory).ToList();
        var parentsWithDirectories = directories.SelectMany(d => Parents(d.RelativePath)).ToHashSet(StringComparer.Ordinal);
        var emptyDirectories = directories.Where(e => !parentsWithFiles.Contains(Glob.Normalize(e.RelativePath)) && !parentsWithDirectories.Contains(Glob.Normalize(e.RelativePath))).ToList();
        foreach (var directory in emptyDirectories)
            BackupExecutor.ValidateSourcePath(System.IO.Path.Combine(source, directory.RelativePath), source, profile.ExcludeLinks);

        string view = System.IO.Path.Combine(work, "view");
        Directory.CreateDirectory(view);
        using var alias = allowAlias && !archive.NativeRootPrefix ? DirectoryAlias.TryCreate(System.IO.Path.Combine(view, rootName), source, report) : null;
        string prefix = alias is null ? "" : rootName + "/";
        string workingDirectory = alias is null ? source : view;
        string files = System.IO.Path.Combine(work, "selection.txt");
        string excluded = System.IO.Path.Combine(work, "empty-directory-contents.txt");
        var selectedPaths = manifest.Select(f => prefix + Glob.Normalize(f.RelativePath))
            .Concat(emptyDirectories.Select(d => prefix + Glob.Normalize(d.RelativePath) + (archive.NativeRootPrefix ? "" : "/")));
        await File.WriteAllLinesAsync(files, selectedPaths.Select(Quote), new UTF8Encoding(false), ct);
        await File.WriteAllLinesAsync(excluded, emptyDirectories.Select(d => Quote(prefix + Glob.Normalize(d.RelativePath) + "/*")), new UTF8Encoding(false), ct);

        report($"Compressing {manifest.Count:N0} files directly from the source; work files contain only metadata.");
        void Log(string line) => report(archive.Name + ": " + line);
        await archive.CreateAsync(archivePath, workingDirectory, files, excluded, profile, rootName, Log, ct);
        var entries = await archive.ListAsync(archivePath, ct);
        ValidateEntries(entries, archive.NativeRootPrefix ? rootName + "/" : prefix, manifest, emptyDirectories);
        if (alias is null && !archive.NativeRootPrefix)
        {
            report("A source folder alias is unavailable. Adding the timestamped root by renaming archive entries; this rewrites compressed archive data once, without copying source files.");
            string volume = System.IO.Path.GetPathRoot(archivePath)!;
            if (!volume.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(volume).AvailableFreeSpace < new FileInfo(archivePath).Length + 16L * 1024 * 1024)
                throw new IOException("Insufficient free space for the archive root-name rewrite. The partial archive was retained.");
            string pairs = System.IO.Path.Combine(work, "rename.txt");
            await File.WriteAllLinesAsync(pairs, entries.SelectMany(e => new[] { Quote(e.Path), Quote(rootName + "/" + Glob.Normalize(e.Path)) }), new UTF8Encoding(false), ct);
            await archive.RenameAsync(archivePath, pairs, Log, ct);
            entries = await archive.ListAsync(archivePath, ct);
        }
        ValidateEntries(entries, rootName + "/", manifest, emptyDirectories);
        foreach (var file in manifest)
        {
            string input = System.IO.Path.Combine(source, file.RelativePath);
            BackupExecutor.ValidateSourcePath(input, source, profile.ExcludeLinks);
            CheckMetadata(input, file.Size, file.LastWriteUtc);
        }
        report("Testing archive integrity…");
        await archive.VerifyAsync(archivePath, Log, ct);
        report("Verifying archived SHA-256 hashes through an in-memory stream…");
        var expected = manifest.ToDictionary(f => rootName + "/" + Glob.Normalize(f.RelativePath), StringComparer.Ordinal);
        await archive.VerifyContentAsync(archivePath, entries, expected, Log, ct);
        return manifest;
    }

    private static string Quote(string path)
    {
        if (path.IndexOfAny(['"', '\r', '\n']) >= 0) throw new IOException("A selected path cannot be represented safely in an archive file list.");
        return '"' + path + '"';
    }

    private static void CheckMetadata(string path, long size, DateTime modified)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != modified)
            throw new IOException($"The source changed after scanning: {path}. Run the backup again.");
    }

    private static void ValidateEntries(IReadOnlyList<ArchiveEntry> entries, string prefix, IReadOnlyList<ManifestFile> manifest, IReadOnlyList<PlanEntry> directories)
    {
        var expected = manifest.ToDictionary(f => prefix + Glob.Normalize(f.RelativePath), f => (Directory: false, f.Size), StringComparer.Ordinal);
        foreach (var directory in directories) expected.Add(prefix + Glob.Normalize(directory.RelativePath), (true, 0));
        foreach (var entry in entries)
        {
            string path = Glob.Normalize(entry.Path);
            if (!expected.Remove(path, out var value) || entry.IsDirectory != value.Directory || entry.Size != value.Size)
                throw new IOException($"Archive selection differs from the approved file list: {entry.Path}");
        }
        if (expected.Count != 0) throw new IOException("The archive is missing approved files or empty directories.");
    }
}
