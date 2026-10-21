namespace Bckinator.Core;

public sealed class BackupScanner
{
    public Task<BackupPlan> ScanAsync(BackupProfile profile, IProgress<BackupProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Scan(profile, progress, ct), ct);

    private static BackupPlan Scan(BackupProfile profile, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var (source, _) = ProfileValidator.ValidatePaths(profile);
        var engine = new SelectionEngine(profile);
        var entries = new List<PlanEntry>();
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        int visited = 0;

        void Visit(string directory)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(source, path);
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                        var decision = engine.Evaluate(relative, isDirectory);
                        bool link = attributes.HasFlag(FileAttributes.ReparsePoint);
                        if (link && profile.ExcludeLinks)
                        {
                            entries.Add(new(relative, isDirectory, 0, default, false, "Symbolic link or junction excluded"));
                            continue;
                        }
                        if (!decision.Included && !decision.Traverse)
                        {
                            entries.Add(new(relative, isDirectory, 0, default, false, decision.Reason));
                            continue;
                        }
                        string physical = ResolvePhysicalPath(path);
                        if (!ProfileValidator.IsSameOrBelow(physical, source))
                        {
                            entries.Add(new(relative, isDirectory, 0, default, false, "Link target is outside the source; skipped", true));
                            continue;
                        }
                        if (isDirectory && ancestors.Contains(physical))
                        {
                            entries.Add(new(relative, true, 0, default, false, "Directory link cycle; skipped", true));
                            continue;
                        }
                        long size = isDirectory ? 0 : new FileInfo(path).Length;
                        DateTime modified = File.GetLastWriteTimeUtc(path);
                        entries.Add(new(relative, isDirectory, size, modified, decision.Included, decision.Reason));
                        if (++visited % 100 == 0) progress?.Report(new($"Scanning · {visited:N0} items inspected"));
                        if (isDirectory && decision.Traverse)
                        {
                            ancestors.Add(physical);
                            try { Visit(path); }
                            finally { ancestors.Remove(physical); }
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex) && profile.ContinueOnErrors)
                    {
                        entries.Add(new(relative, false, 0, default, false, ex.Message, true));
                    }
                }
            }
            catch (Exception ex) when (IsFileError(ex) && profile.ContinueOnErrors)
            {
                entries.Add(new(Path.GetRelativePath(source, directory), true, 0, default, false, $"Cannot enumerate directory: {ex.Message}", true));
            }
        }

        progress?.Report(new("Scanning source…"));
        Visit(source);
        return new(entries);
    }

    public static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException;

    public static string ResolvePhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string resolved = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var attributes = File.GetAttributes(resolved);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
                resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Cannot resolve link target: {resolved}");
            }
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }
}
