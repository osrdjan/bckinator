using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Bckinator.Core;

namespace Bckinator.Tests;

public sealed partial class ExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Bckinator.Tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "destination");
    private string Runs => Path.Combine(root, "runs");
    public ExecutionTests() => Directory.CreateDirectory(Source);

    private void Write(string path, string content = "sample")
    {
        string full = Path.Combine(Source, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
    private BackupProfile Profile(OutputFormat format = OutputFormat.Folder)
    {
        var profile = ProfileStore.CreateSample();
        profile.Source = Source; profile.Destination = Destination;
        profile.NamingPattern = "snapshot"; profile.Format = format;
        profile.Compression = new() { Level = 1, DictionaryMb = 1, FastBytes = 32, Threads = 1, SolidBlockGb = 1 };
        return profile;
    }
    private Task<BackupRun> Run(BackupProfile profile, IProgress<BackupProgress>? progress = null, CancellationToken ct = default, AppSettings? settings = null) =>
        new BackupExecutor(new()).ExecuteAsync(profile, settings ?? new(), Runs, progress, ct);

    [Fact]
    public async Task FolderBackupMatchesPreviewPreservesSourceAndTimestampsAndWritesManifest()
    {
        Write("file.txt", "original"); Write(".idea/exclude.txt"); Write("diag_tools/diagrams/design.txt"); Write("diag_tools/unused.txt"); Write("tool.exe");
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var profile = Profile();
        var plan = await new BackupScanner().ScanAsync(profile);
        var run = await Run(profile);
        Assert.Equal(RunStatus.Completed, run.Status);
        string[] expected = plan.Entries.Where(e => e.Included && !e.IsDirectory).Select(e => e.RelativePath).Order().ToArray();
        string[] actual = Directory.GetFiles(run.OutputPath, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(run.OutputPath, p)).Order().ToArray();
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(Path.Combine(run.OutputPath, "empty")));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(Source, "file.txt")), File.GetLastWriteTimeUtc(Path.Combine(run.OutputPath, "file.txt")));
        foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(run.OutputPath + ".manifest.json"), ProfileStore.JsonOptions)!;
        Assert.Equal(expected.Length, manifest.Files.Count);
        Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-staging-*"));
        var collision = await Run(profile);
        Assert.Equal(RunStatus.Failed, collision.Status);
        Assert.Contains("already exists", collision.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(run.OutputPath, "file.txt")));
    }

    [Fact]
    public async Task DestinationInsideSourceIsRejectedBeforeWriting()
    {
        Write("file.txt"); var profile = Profile(); profile.Destination = Path.Combine(Source, "backups");
        var run = await Run(profile);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("outside the source", run.Message);
        Assert.False(Directory.Exists(profile.Destination));
    }

    [Fact]
    public async Task MissingSourceAndEmptySelectionAreClearFailures()
    {
        var profile = Profile();
        profile.Source = Path.Combine(root, "missing");
        var missing = await Run(profile);
        Assert.Equal(RunStatus.Failed, missing.Status);
        Assert.Contains("does not exist", missing.Message);
        var empty = await Run(Profile());
        Assert.Equal(RunStatus.Failed, empty.Status);
        Assert.Contains("No files match", empty.Message);
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task JunctionsAreSkippedByDefaultAndExternalTargetsAndCyclesAreReported()
    {
        Write("file.txt");
        string external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "external.txt"), "must not copy");
        var links = new[] { (Path.Combine(Source, "external-link"), external), (Path.Combine(Source, "cycle"), Source) };
        foreach (var (link, target) in links)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            var reads = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync(); await reads;
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var profile = Profile();
            var defaultPlan = await new BackupScanner().ScanAsync(profile);
            Assert.Equal(1, defaultPlan.FileCount);
            Assert.Equal(0, defaultPlan.WarningCount);
            profile.ExcludeLinks = false;
            var followPlan = await new BackupScanner().ScanAsync(profile);
            Assert.Equal(1, followPlan.FileCount);
            Assert.Equal(2, followPlan.WarningCount);
            profile.Destination = Path.Combine(Source, "external-link", "backup");
            Assert.Throws<ArgumentException>(() => ProfileValidator.ValidatePaths(profile));
        }
        finally
        {
            // Nonrecursive junction removal removes only the test-owned directory entries.
            foreach (var (link, _) in links) Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public async Task CancellationRetainsStagingAndNeverPublishesFinalOutput()
    {
        Write("file.txt"); using var ct = new CancellationTokenSource();
        var run = await Run(Profile(), new InlineProgress(p => { if (p.Operation.StartsWith("Staging", StringComparison.Ordinal)) ct.Cancel(); }), ct.Token);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.True(Directory.Exists(run.RetainedStagingPath));
        Assert.False(Directory.Exists(Path.Combine(Destination, "snapshot")));
        Assert.True(File.Exists(Path.Combine(Source, "file.txt")));
    }

    [Fact]
    public async Task CancellationStopsSevenZipWithoutCreatingSourceCopies()
    {
        Write("file.txt", new string('x', 10000));
        using var ct = new CancellationTokenSource();
        var run = await Run(Profile(OutputFormat.SevenZip), new InlineProgress(p =>
        {
            if (p.Operation.StartsWith("7-Zip:", StringComparison.Ordinal)) ct.Cancel();
        }), ct.Token);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.True(Directory.Exists(run.RetainedStagingPath));
        Assert.True(File.Exists(Path.Combine(run.RetainedStagingPath, "selection.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(run.RetainedStagingPath, "view")));
        Assert.True(File.Exists(Path.Combine(Source, "file.txt")));
        Assert.False(File.Exists(run.OutputPath));
    }

    [Fact]
    public async Task SourceChangeAfterScanningPreventsPublication()
    {
        Write("file.txt");
        var run = await Run(Profile(), new InlineProgress(p =>
        {
            if (p.Operation.StartsWith("Staging", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(Source, "file.txt"), "changed after scan");
        }));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("changed after scanning", run.Message);
        Assert.False(Directory.Exists(run.OutputPath));
    }

    [Fact]
    public async Task LockedFileFailsByDefaultAndMayBeSkippedWithExplicitWarnings()
    {
        Write("locked.txt"); Write("available.txt");
        using var locked = new FileStream(Path.Combine(Source, "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var profile = Profile();
        var failed = await Run(profile);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.True(Directory.Exists(failed.RetainedStagingPath));
        profile.ContinueOnErrors = true;
        var partial = await Run(profile);
        Assert.Equal(RunStatus.CompletedWithWarnings, partial.Status);
        Assert.Equal(1, partial.WarningCount);
        Assert.True(File.Exists(Path.Combine(partial.OutputPath, "available.txt")));
        Assert.False(File.Exists(Path.Combine(partial.OutputPath, "locked.txt")));
    }

    [Fact]
    public async Task RealSevenZipBackupExtractsTheExpectedRootAndContent()
    {
        string exe = new AppSettings().SevenZipPath;
        if (!File.Exists(exe)) throw new InvalidOperationException("Install 7-Zip to run the archive integration test.");
        Write("file.txt", "archive content"); Write("diag_tools/diagrams/design.txt"); Write("diag_tools/diagrams/tool.exe");
        var profile = Profile(OutputFormat.SevenZip);
        profile.NamingPattern = "snapshot space ü";
        var run = await Run(profile);
        Assert.True(run.Status == RunStatus.Completed, run.Message + " " + run.LogPath);
        string extract = Path.Combine(root, "extract");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "x", "-y", "-o" + extract, "--", run.OutputPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        await Task.WhenAll(stdout, stderr);
        Assert.Equal("archive content", await File.ReadAllTextAsync(Path.Combine(extract, profile.NamingPattern, "file.txt")));
        Assert.False(File.Exists(Path.Combine(extract, profile.NamingPattern, "diag_tools", "diagrams", "tool.exe")));
        Assert.Single(Directory.GetDirectories(extract));
        Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-staging-*"));
    }

    [Fact]
    public async Task ExternalProcessFailureKeepsMetadataAndProducesNoFinalArchive()
    {
        Write("file.txt");
        var settings = new AppSettings { SevenZipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe") };
        var run = await Run(Profile(OutputFormat.SevenZip), settings: settings);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("exit code", run.Message);
        Assert.True(File.Exists(Path.Combine(run.RetainedStagingPath, "selection.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(run.RetainedStagingPath, "view")));
        Assert.True(File.Exists(Path.Combine(Source, "file.txt")));
        Assert.False(File.Exists(run.OutputPath));
    }

    [Fact]
    public async Task StoreUsesVersionedAtomicDocumentsAndPreservesUnsupportedFiles()
    {
        var store = new ProfileStore(Path.Combine(root, "config"));
        await store.WriteAsync("profiles.json", new[] { Profile() });
        Assert.Single((await store.ReadAsync<BackupProfile[]>("profiles.json"))!);
        Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
        string path = Path.Combine(store.Root, "profiles.json");
        string unsupported = (await File.ReadAllTextAsync(path)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 42");
        await File.WriteAllTextAsync(path, unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync<BackupProfile[]>("profiles.json"));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        string absolute = Path.GetFullPath(root);
        string parent = Path.Combine(Path.GetTempPath(), "Bckinator.Tests");
        if (!ProfileValidator.IsSameOrBelow(absolute, parent)) throw new InvalidOperationException("Test cleanup boundary failed.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
    private sealed class InlineProgress(Action<BackupProgress> handler) : IProgress<BackupProgress> { public void Report(BackupProgress value) => handler(value); }
}
