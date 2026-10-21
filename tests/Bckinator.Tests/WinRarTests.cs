using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Bckinator.Core;

namespace Bckinator.Tests;

public sealed partial class ExecutionTests
{
    [Fact]
    public void RarDefaultsAndCommandMatchBestCompressionWithoutUserConfiguration()
    {
        var profile = Profile();
        Assert.Equal(RarCompressionMethod.Best, profile.RarCompression.Method);
        Assert.Equal(256, profile.RarCompression.DictionaryMb);
        Assert.True(profile.RarCompression.Solid);
        Assert.True(profile.RarCompression.RecoveryRecord);
        Assert.Equal(5, profile.RarCompression.RecoveryRecordPercent);
        Assert.True(profile.RarCompression.LongRangeSearch);
        Assert.True(profile.RarCompression.UseBlake2);
        var args = WinRar.CreateArguments("out.rar", "files.txt", "root", profile.RarCompression);
        Assert.Contains("-m5", args); Assert.Contains("-md256m", args); Assert.Contains("-s", args);
        Assert.Contains("-cfg-", args); Assert.Contains("-r-", args); Assert.Contains("-aproot", args);
        foreach (string flag in new[] { "-ma5", "-rr5p", "-t", "-mcl+", "-mcd", "-mce", "-mcx-", "-htb", "-qo", "-oi0", "-o+" }) Assert.Contains(flag, args);
        foreach (string flag in new[] { "-df", "-dr", "-dw", "-sfx", "-k", "-v", "-ow", "-os", "-ol", "-oh", "-ams", "-ibck", "-ioff", "-p", "-hp" })
            Assert.DoesNotContain(args, a => a.StartsWith(flag, StringComparison.Ordinal));
        // Old v1 documents omit RAR settings and receive the new defaults on import.
        var legacy = ProfileStore.Export(profile);
        var document = System.Text.Json.Nodes.JsonNode.Parse(legacy)!;
        document["data"]!.AsObject().Remove("rarCompression");
        var imported = ProfileStore.Import(document.ToJsonString());
        Assert.Equal(RarCompressionMethod.Best, imported.RarCompression.Method);
        Assert.Equal(256, imported.RarCompression.DictionaryMb);
        Assert.True(imported.RarCompression.Solid);
        imported.Format = OutputFormat.WinRar;
        imported.RarCompression.Method = RarCompressionMethod.Good;
        imported.RarCompression.Solid = true;
        var roundTrip = ProfileStore.Import(ProfileStore.Export(imported));
        Assert.Equal(OutputFormat.WinRar, roundTrip.Format);
        Assert.Equal(RarCompressionMethod.Good, roundTrip.RarCompression.Method);
        Assert.True(roundTrip.RarCompression.Solid);
        // Explicit settings saved by the previous version remain profile overrides.
        imported.RarCompression.DictionaryMb = 32; imported.RarCompression.Solid = false;
        roundTrip = ProfileStore.Import(ProfileStore.Export(imported));
        Assert.Equal(32, roundTrip.RarCompression.DictionaryMb);
        Assert.False(roundTrip.RarCompression.Solid);
        roundTrip.RarCompression.ResetToDefaults();
        Assert.Equal(256, roundTrip.RarCompression.DictionaryMb);
        Assert.True(roundTrip.RarCompression.Solid);
    }

    [Fact]
    public void RarRecoverySpaceEstimateIncludesTheConfiguredPercentage()
    {
        var profile = Profile(OutputFormat.WinRar);
        long withRecovery = BackupExecutor.EstimateRequiredBytes(1_000_003, profile);
        profile.RarCompression.RecoveryRecord = false;
        long withoutRecovery = BackupExecutor.EstimateRequiredBytes(1_000_003, profile);
        Assert.Equal((long)Math.Ceiling(withoutRecovery * 1.05m), withRecovery);
        var args = WinRar.CreateArguments("out.rar", "list", "root", profile.RarCompression);
        Assert.DoesNotContain(args, a => a.StartsWith("-rr", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)] [InlineData(1001)]
    public void InvalidRecoveryRecordSizesAreRejected(int percent)
    {
        var profile = Profile(OutputFormat.WinRar);
        profile.RarCompression.RecoveryRecordPercent = percent;
        Assert.Throws<ArgumentException>(() => ProfileValidator.ValidateDefinition(profile));
    }

    [RarFact]
    public async Task RarDefaultsProduceAnUnencryptedSolidRar5ArchiveWithBlake2AndFivePercentRecovery()
    {
        Write("first.txt", new string('x', 300000)); Write("identical.txt", new string('x', 300000));
        var run = await Run(Profile(OutputFormat.WinRar));
        Assert.True(run.Status == RunStatus.Completed, run.Message);
        string listing = await RarOutput("lta", run.OutputPath);
        Assert.Contains("RAR 5, solid, recovery record", listing);
        Assert.Contains("BLAKE2", listing);
        Assert.Contains("RR%: 5%", listing);
        Assert.DoesNotContain("encrypted", listing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reference", listing, StringComparison.OrdinalIgnoreCase);
        // This independent extraction supplies no password and closes stdin, so any
        // accidental encryption fails instead of being hidden by our verification flags.
        string extract = Path.Combine(root, "extracted") + Path.DirectorySeparatorChar;
        await RarOutput("x", run.OutputPath, extract);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Source, "first.txt")), await File.ReadAllTextAsync(Path.Combine(extract, "snapshot", "first.txt")));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Source, "identical.txt")), await File.ReadAllTextAsync(Path.Combine(extract, "snapshot", "identical.txt")));
    }

    private static async Task<string> RarOutput(string command, string archivePath, string? destination = null)
    {
        var start = new ProcessStartInfo(new AppSettings().RarPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { command, "-cfg-", "-scfr", "-idc", "-y", "--", archivePath }) start.ArgumentList.Add(arg);
        if (destination is not null) start.ArgumentList.Add(destination);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        string result = await output + await error;
        Assert.True(process.ExitCode == 0, result);
        return result;
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
