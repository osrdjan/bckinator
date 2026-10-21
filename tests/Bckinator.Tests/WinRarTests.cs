using System.Security.Cryptography;
using System.Text.Json;
using Bckinator.Core;

namespace Bckinator.Tests;

public sealed partial class ExecutionTests
{
    [Fact]
    public void RarDefaultsAndCommandMatchBestCompressionWithoutUserConfiguration()
    {
        var profile = Profile();
        Assert.Equal(RarCompressionMethod.Best, profile.RarCompression.Method);
        Assert.Equal(32, profile.RarCompression.DictionaryMb);
        Assert.False(profile.RarCompression.Solid);
        var args = WinRar.CreateArguments("out.rar", "files.txt", "root", profile.RarCompression);
        Assert.Contains("-m5", args); Assert.Contains("-md32m", args); Assert.Contains("-s-", args);
        Assert.Contains("-cfg-", args); Assert.Contains("-r-", args); Assert.Contains("-aproot", args);
        Assert.DoesNotContain("-df", args);
        // Old v1 documents omit RAR settings and receive the new defaults on import.
        var legacy = ProfileStore.Export(profile);
        var document = System.Text.Json.Nodes.JsonNode.Parse(legacy)!;
        document["data"]!.AsObject().Remove("rarCompression");
        var imported = ProfileStore.Import(document.ToJsonString());
        Assert.Equal(RarCompressionMethod.Best, imported.RarCompression.Method);
        imported.Format = OutputFormat.WinRar;
        imported.RarCompression.Method = RarCompressionMethod.Good;
        imported.RarCompression.Solid = true;
        var roundTrip = ProfileStore.Import(ProfileStore.Export(imported));
        Assert.Equal(OutputFormat.WinRar, roundTrip.Format);
        Assert.Equal(RarCompressionMethod.Good, roundTrip.RarCompression.Method);
        Assert.True(roundTrip.RarCompression.Solid);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(2048)]
    public void InvalidRarDictionariesAreRejected(int size)
    {
        var profile = Profile(OutputFormat.WinRar);
        profile.RarCompression.DictionaryMb = size;
        Assert.Throws<ArgumentException>(() => ProfileValidator.ValidateDefinition(profile));
    }

    [RarTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task RarBackupsVerifyContentSelectionAndRootWithoutStagingOrAliases(bool solid)
    {
        Write("@notes.txt", "literal list syntax"); Write("-options.txt", "literal option syntax");
        Write(" leading space.txt", "whitespace"); Write("nested ü/čćש.txt", new string('a', 180000));
        Write("zero.txt", ""); Write("tool.exe", "excluded"); Write(".idea/config.xml", "excluded");
        for (int i = 0; i < 12; i++) Write($"mixed/{i}.bin", new string((char)('a' + i), i * 713));
        Directory.CreateDirectory(Path.Combine(Source, "empty-parent", "leaf"));
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var profile = Profile(OutputFormat.WinRar); profile.RarCompression.Solid = solid;
        bool observed = false;
        var run = await Run(profile, new InlineProgress(p =>
        {
            Assert.False(p.Operation.StartsWith("Staging ", StringComparison.Ordinal));
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            observed = true;
            Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-staging-*"));
            string work = Assert.Single(Directory.GetDirectories(Destination, ".bckinator-work-*"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(work, "view")));
            Assert.All(Directory.GetFiles(work), f => Assert.EndsWith(".txt", f));
            Write("empty-parent/leaf/late.txt", "must stay excluded");
        }), settings: new() { SevenZipPath = "missing-7zip.exe" });
        Assert.True(observed);
        Assert.True(run.Status == RunStatus.Completed, run.Message);
        Assert.EndsWith(".rar", run.OutputPath);
        Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-work-*"));
        foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var entries = await new WinRar(new AppSettings().RarPath).ListAsync(run.OutputPath, CancellationToken.None);
        Assert.All(entries, e => Assert.StartsWith("snapshot/", Glob.Normalize(e.Path)));
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/empty-parent/leaf" && e.IsDirectory);
        Assert.DoesNotContain(entries, e => e.Path.Contains("late.txt") || e.Path.Contains("tool.exe") || e.Path.Contains(".idea"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(run.OutputPath + ".manifest.json"), ProfileStore.JsonOptions)!;
        Assert.Equal(17, manifest.Files.Count);
        foreach (var file in manifest.Files) Assert.Equal(before[Path.Combine(Source, file.RelativePath)], file.Sha256);
        var collision = await Run(profile);
        Assert.Equal(RunStatus.Failed, collision.Status);
        Assert.Contains("already exists", collision.Message);
    }

    [RarFact]
    public async Task RarHashVerificationRejectsChangedContentEvenWithOriginalMetadata()
    {
        Write("change.txt", "before");
        string path = Path.Combine(Source, "change.txt"); DateTime modified = File.GetLastWriteTimeUtc(path);
        var run = await Run(Profile(OutputFormat.WinRar), new InlineProgress(p =>
        {
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            File.WriteAllText(path, "after!"); File.SetLastWriteTimeUtc(path, modified);
        }));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("SHA-256", run.Message);
        Assert.False(File.Exists(run.OutputPath));
        Assert.True(Directory.Exists(run.RetainedStagingPath));
        Assert.Single(Directory.GetFiles(Destination, "*.partial.rar"));
    }

    [RarFact]
    public async Task RarRejectsFileReplacedWithDirectoryAndKeepsPartialArchive()
    {
        Write("changed.txt");
        var run = await Run(Profile(OutputFormat.WinRar), new InlineProgress(p =>
        {
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            File.Delete(Path.Combine(Source, "changed.txt"));
            Write("changed.txt/secret.txt", "must not be included");
        }));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.False(File.Exists(run.OutputPath));
        var entries = await new WinRar(new AppSettings().RarPath).ListAsync(Assert.Single(Directory.GetFiles(Destination, "*.partial.rar")), CancellationToken.None);
        Assert.DoesNotContain(entries, e => e.Path.Contains("secret.txt"));
    }

    [Fact]
    public async Task MissingWinRarHasActionableErrorBeforeWritingDestination()
    {
        Write("file.txt");
        var run = await Run(Profile(OutputFormat.WinRar), settings: new() { RarPath = Path.Combine(root, "missing.exe") });
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("Rar.exe", run.Message);
        Assert.False(Directory.Exists(Destination));
    }
}

public sealed class RarFactAttribute : FactAttribute
{
    public RarFactAttribute() { if (!File.Exists(new AppSettings().RarPath)) Skip = "WinRAR Rar.exe is not installed at its default location."; }
}

public sealed class RarTheoryAttribute : TheoryAttribute
{
    public RarTheoryAttribute() { if (!File.Exists(new AppSettings().RarPath)) Skip = "WinRAR Rar.exe is not installed at its default location."; }
}
