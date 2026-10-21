using System.Text;
using System.Text.RegularExpressions;

namespace Bckinator.Core;

public static class Glob
{
    public static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    public static bool Matches(string pattern, string path)
    {
        pattern = Normalize(pattern);
        var expression = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { expression.Append("(?:.*/)?"); i++; }
                else expression.Append(".*");
            }
            else if (pattern[i] == '*') expression.Append("[^/]*");
            else if (pattern[i] == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(pattern[i].ToString()));
        }
        expression.Append('$');
        return Regex.IsMatch(Normalize(path), expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    }

    public static bool SameOrBelow(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    public static bool Includes(string pattern, string path)
    {
        pattern = Normalize(pattern); path = Normalize(path);
        if (!pattern.Contains('*') && !pattern.Contains('?')) return SameOrBelow(path, pattern);
        return Matches(pattern, path) || (pattern.EndsWith("/**", StringComparison.Ordinal) && path.Equals(pattern[..^3], StringComparison.OrdinalIgnoreCase));
    }

    public static bool CouldContain(string pattern, string directory)
    {
        pattern = Normalize(pattern); directory = Normalize(directory);
        if (Includes(pattern, directory)) return true;
        int wildcard = pattern.IndexOfAny(['*', '?']);
        if (wildcard < 0) return SameOrBelow(pattern, directory);
        int slash = pattern.LastIndexOf('/', wildcard);
        string prefix = slash < 0 ? "" : pattern[..slash];
        return prefix.Length == 0 || SameOrBelow(prefix, directory) || SameOrBelow(directory, prefix);
    }
}

public sealed class SelectionEngine(BackupProfile profile)
{
    public SelectionDecision Evaluate(string relativePath, bool isDirectory)
    {
        string path = Glob.Normalize(relativePath);
        foreach (var rule in profile.Rules.Where(r => r.Enabled && r.Action == RuleAction.Exclude))
            if (Matches(rule, path, isDirectory)) return new(false, false, $"Excluded by {rule.Kind}: {rule.Pattern}");

        if (profile.IncludeOnly)
        {
            var includes = profile.Rules.Where(r => r.Enabled && r.Action == RuleAction.Include).ToArray();
            if (!includes.Any(r => Matches(r, path, isDirectory)))
            {
                bool traverse = isDirectory && includes.Any(r => CouldMatchBelow(r, path));
                if (!traverse) return new(false, false, "No Include rule matched");
            }
        }

        foreach (var scope in profile.Scopes.Where(s => s.Enabled))
        {
            string directory = Glob.Normalize(scope.RelativeDirectory);
            if (path.Equals(directory, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Glob.SameOrBelow(path, directory)) continue;
            string local = path[(directory.Length + 1)..];
            if (!scope.Patterns.Any(pattern => Glob.Includes(pattern, local)))
            {
                bool traverse = isDirectory && scope.Patterns.Any(pattern => Glob.CouldContain(pattern, local));
                return new(false, traverse, $"Outside include-only selection in {directory}");
            }
        }
        return new(true, isDirectory, "Included by profile selection");
    }

    private static bool Matches(SelectionRule rule, string path, bool isDirectory) => rule.Kind switch
    {
        RuleKind.DirectoryName => path.Split('/').Take(isDirectory ? path.Split('/').Length : path.Split('/').Length - 1).Any(part => Glob.Matches(rule.Pattern, part)),
        RuleKind.FilePattern => !isDirectory && Glob.Matches(rule.Pattern, path.Split('/')[^1]),
        RuleKind.RelativePath => Glob.SameOrBelow(path, Glob.Normalize(rule.Pattern)),
        RuleKind.PathGlob => Glob.Includes(rule.Pattern, path),
        _ => false
    };

    private static bool CouldMatchBelow(SelectionRule rule, string path) => rule.Kind switch
    {
        RuleKind.DirectoryName or RuleKind.FilePattern => true,
        _ => Glob.CouldContain(rule.Pattern, path)
    };
}
