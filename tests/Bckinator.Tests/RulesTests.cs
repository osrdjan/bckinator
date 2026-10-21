using Bckinator.Core;

namespace Bckinator.Tests;

public sealed class RulesTests
{
    [Theory]
    [InlineData("src/main.cs", true)]
    [InlineData("src/.venv/inner/code.py", false)]
    [InlineData("src/.IDEA/misc.xml", false)]
    [InlineData("diag_tools/diagrams/system.png", true)]
    [InlineData("diag_tools/diagrams/tool.EXE", false)]
    [InlineData("diag_tools/ConsoleHost_history.txt", true)]
    [InlineData("diag_tools/unused.txt", false)]
    [InlineData("elsewhere/diag_tools/unused.txt", true)]
    [InlineData("src/__pycache__/inner.pyc", false)]
    public void SampleSelectionHasPredictablePrecedence(string path, bool expected)
    {
        Assert.Equal(expected, new SelectionEngine(ProfileStore.CreateSample()).Evaluate(path, false).Included);
    }

    [Theory]
    [InlineData("**/*.txt", "note.txt", true)]
    [InlineData("**/*.txt", "a/b/NOTE.TXT", true)]
    [InlineData("*.txt", "a/note.txt", false)]
    [InlineData("src/?ile.cs", "src/file.cs", true)]
    [InlineData("src/**", "src/nested/a.cs", true)]
    [InlineData("a+b.txt", "a+b.txt", true)]
    public void GlobsAreCaseInsensitiveAndPathAware(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.Matches(pattern, path));

    [Fact]
    public void NestedScopesIntersectAndGlobalExclusionsWin()
    {
        var profile = ProfileStore.CreateSample();
        profile.Scopes.Add(new() { RelativeDirectory = "diag_tools/diagrams", IncludePatterns = "published/" });
        var engine = new SelectionEngine(profile);
        Assert.False(engine.Evaluate("diag_tools/diagrams/draft.png", false).Included);
        Assert.True(engine.Evaluate("diag_tools/diagrams/published/ready.png", false).Included);
        Assert.False(engine.Evaluate("diag_tools/diagrams/published/tool.exe", false).Included);
    }

    [Fact]
    public void IncludeOnlyStillTraversesAncestorsAndPrunesOtherBranches()
    {
        var profile = new BackupProfile { IncludeOnly = true, Rules = [new() { Action = RuleAction.Include, Kind = RuleKind.RelativePath, Pattern = "src/deep/file.cs" }] };
        var engine = new SelectionEngine(profile);
        Assert.True(engine.Evaluate("src", true).Traverse);
        Assert.True(engine.Evaluate("src/deep", true).Traverse);
        Assert.True(engine.Evaluate("src/deep/file.cs", false).Included);
        Assert.False(engine.Evaluate("other", true).Traverse);
        Assert.False(engine.Evaluate("src/deep/other.cs", false).Included);
    }

    [Fact]
    public void DisablingAndReorderingPreserveDefinedPrecedence()
    {
        var profile = ProfileStore.CreateSample();
        profile.Rules.Add(new() { Action = RuleAction.Include, Kind = RuleKind.RelativePath, Pattern = "tool.exe" });
        Assert.False(new SelectionEngine(profile).Evaluate("tool.exe", false).Included);
        profile.Rules.Move(4, 0);
        Assert.False(new SelectionEngine(profile).Evaluate("tool.exe", false).Included);
        profile.Rules.Single(r => r.Pattern == "*.exe").Enabled = false;
        Assert.True(new SelectionEngine(profile).Evaluate("tool.exe", false).Included);
    }

    [Fact]
    public void ScopedGlobTraversesRequiredAncestorDirectories()
    {
        var profile = new BackupProfile { Scopes = [new() { RelativeDirectory = "tools", IncludePatterns = "reports/published/**/*.txt" }] };
        var engine = new SelectionEngine(profile);
        Assert.True(engine.Evaluate("tools/reports", true).Traverse);
        Assert.False(engine.Evaluate("tools/scratch", true).Traverse);
        Assert.True(engine.Evaluate("tools/reports/published/a.txt", false).Included);
        Assert.False(engine.Evaluate("tools/reports/published/a.exe", false).Included);
    }

    [Fact]
    public void JsonRoundtripPreservesProfileAndImportAssignsNewIdentity()
    {
        var original = ProfileStore.CreateSample();
        var clone = ProfileStore.Clone(original);
        Assert.Equal(ProfileStore.Export(original), ProfileStore.Export(clone));
        var imported = ProfileStore.Import(ProfileStore.Export(original));
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(original.Scopes[0].IncludePatterns, imported.Scopes[0].IncludePatterns);
        Assert.Equal(256, imported.Compression.DictionaryMb);
        Assert.Throws<InvalidDataException>(() => ProfileStore.Import(ProfileStore.Export(original).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99")));
    }

    [Fact]
    public void NamingUsesOneTimestampAndRejectsUnsafeNames()
    {
        var when = new DateTimeOffset(2026, 10, 21, 17, 4, 0, TimeSpan.FromHours(2));
        Assert.Equal("wocs_2610211704", BackupNaming.Generate("wocs_{yyMMddHHmm}", when));
        foreach (string pattern in new[] { "../backup", "x/{yy}", "CON", "NUL.7z", "trailing.", "{unclosed" })
            Assert.ThrowsAny<ArgumentException>(() => BackupNaming.Generate(pattern, when));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("/absolute")]
    [InlineData("x/../outside")]
    public void RulePathsCannotEscapeTheSource(string pattern)
    {
        var profile = new BackupProfile { Source = @"C:\source", Destination = @"C:\backups", Rules = [new() { Kind = RuleKind.RelativePath, Pattern = pattern }] };
        var exception = Assert.Throws<ArgumentException>(() => ProfileValidator.ValidateDefinition(profile));
        Assert.Contains("Rule pattern", exception.Message);
    }

    [Theory]
    [InlineData("C:\\anything", "C:\\", true)]
    [InlineData("C:\\source\\backup", "C:\\source", true)]
    [InlineData("C:\\source-other", "C:\\source", false)]
    public void PathBoundariesIncludeDriveRoots(string path, string parent, bool expected) => Assert.Equal(expected, ProfileValidator.IsSameOrBelow(path, parent));
}
