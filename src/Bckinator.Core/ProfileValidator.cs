using System.Globalization;
using System.Text.RegularExpressions;

namespace Bckinator.Core;

public static class BackupNaming
{
    public static string Generate(string pattern, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Enter an output name pattern.");
        string result = Regex.Replace(pattern, @"\{([^{}]+)\}", match => timestamp.ToString(match.Groups[1].Value, CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(result) || result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || result.Contains('{') || result.Contains('}') || result is "." or ".." || result.EndsWith('.') || result.EndsWith(' ') || result.Length > 180)
            throw new ArgumentException("The output name must be a single valid filename (up to 180 characters).");
        string stem = result.Split('.')[0];
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("The output name uses a reserved Windows device name.");
        return result;
    }
}

public static class ProfileValidator
{
    public static void ValidateDefinition(BackupProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Enter a profile name.");
        if (string.IsNullOrWhiteSpace(profile.Source) || string.IsNullOrWhiteSpace(profile.Destination) || !Path.IsPathFullyQualified(profile.Source) || !Path.IsPathFullyQualified(profile.Destination))
            throw new ArgumentException("Source and destination must be absolute paths.");
        if (!Enum.IsDefined(profile.Format)) throw new ArgumentException("Select a supported output format.");
        if (profile.Compression is null || profile.RarCompression is null || profile.Rules is null || profile.Scopes is null) throw new ArgumentException("Profile settings, rules, and scopes are required.");
        BackupNaming.Generate(profile.NamingPattern, DateTimeOffset.Now);
        foreach (var rule in profile.Rules)
        {
            if (rule is null || !Enum.IsDefined(rule.Kind) || !Enum.IsDefined(rule.Action)) throw new ArgumentException("A selection rule has an invalid type or action.");
            if (!rule.Enabled) continue;
            ValidateRelative(rule.Pattern, "Rule pattern");
            if (rule.Kind is RuleKind.DirectoryName or RuleKind.FilePattern && (rule.Pattern.Contains('/') || rule.Pattern.Contains('\\')))
                throw new ArgumentException("Directory names and filename patterns must not contain path separators. Use RelativePath or PathGlob for paths.");
        }
        foreach (var scope in profile.Scopes)
        {
            if (scope is null) throw new ArgumentException("A directory scope is invalid.");
            if (!scope.Enabled) continue;
            ValidateRelative(scope.RelativeDirectory, "Scoped directory");
            if (scope.RelativeDirectory.IndexOfAny(['*', '?']) >= 0) throw new ArgumentException("A scoped directory must be a literal relative directory.");
            if (scope.Patterns.Length == 0) throw new ArgumentException("Enter at least one included path for each enabled directory scope.");
            foreach (string pattern in scope.Patterns) ValidateRelative(pattern, "Scoped inclusion");
        }
        var c = profile.Compression;
        if (c.Level is < 0 or > 9 || c.DictionaryMb is < 1 or > 1536 || c.FastBytes is < 5 or > 273 || c.SolidBlockGb is < 1 or > 1024 || c.Threads is < 1 or > 256)
            throw new ArgumentException("Compression limits: level 0–9, dictionary 1–1536 MB, fast bytes 5–273, solid block 1–1024 GB, threads 1–256.");
        var r = profile.RarCompression;
        if (!Enum.IsDefined(r.Method) || r.DictionaryMb is < 1 or > 1024 || (r.DictionaryMb & (r.DictionaryMb - 1)) != 0)
            throw new ArgumentException("WinRAR requires a supported compression method and a dictionary of 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, or 1024 MB.");
    }

    public static void ValidateRelative(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Split(['/', '\\']).Any(p => p is ".." or "."))
            throw new ArgumentException($"{label} must be a nonempty relative path without '.' or '..' segments.");
    }

    public static (string Source, string Destination) ValidatePaths(BackupProfile profile)
    {
        ValidateDefinition(profile);
        if (!Path.IsPathFullyQualified(profile.Source) || !Path.IsPathFullyQualified(profile.Destination)) throw new ArgumentException("Source and destination must be absolute paths.");
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.Source));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.Destination));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Source directory does not exist: {source}");
        if (IsSameOrBelow(destination, source)) throw new ArgumentException("Choose a destination outside the source directory to prevent recursive backups.");
        // Reject path aliases at the boundary; optional internal links are handled by the scanner.
        RejectReparseAncestors(source);
        RejectReparseAncestors(destination);
        return (source, destination);
    }

    public static bool IsSameOrBelow(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"Source and destination paths must not pass through symbolic links or junctions: {current.FullName}");
    }
}
