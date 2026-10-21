using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Bckinator.Core;

namespace Bckinator.Tests;

public sealed partial class ExecutionTests
{
    [Fact]
    public async Task DirectArchiveWritesOnlyMetadataAndArchiveAndPreservesTheSource()
    {
        Write("@notes.txt", "list file syntax must remain literal");
        Write("-settings.txt", "option syntax must remain literal");
        Write(" leading-space.txt", "spaces must remain literal");
        Write("nested ü/plain.bin", new string('x', 200000));
        Write("empty-file.txt", "");
        Write("tool.exe", "excluded");
        Write(".idea/config.xml", "excluded directory");
        Directory.CreateDirectory(Path.Combine(Source, "empty-parent", "empty-child"));
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        bool observedDirect = false;
        var run = await Run(Profile(OutputFormat.SevenZip), new InlineProgress(p =>
        {
            Assert.False(p.Operation.StartsWith("Staging ", StringComparison.Ordinal));
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            observedDirect = true;
            Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-staging-*"));
            string work = Assert.Single(Directory.GetDirectories(Destination, ".bckinator-work-*"));
            Assert.Equal(new[] { "empty-directory-contents.txt", "selection.txt" }, Directory.GetFiles(work).Select(Path.GetFileName).Order());
            string alias = Assert.Single(Directory.GetDirectories(Path.Combine(work, "view")));
            Assert.True(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
        }));
        Assert.True(observedDirect);
        Assert.True(run.Status == RunStatus.Completed, run.Message);
        Assert.Empty(Directory.GetDirectories(Destination, ".bckinator-work-*"));
        foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(run.OutputPath + ".manifest.json"), ProfileStore.JsonOptions)!;
        Assert.Equal(5, manifest.Files.Count);
        foreach (var file in manifest.Files) Assert.Equal(before[Path.Combine(Source, file.RelativePath)], file.Sha256);
        var entries = await new SevenZip(new AppSettings().SevenZipPath).ListAsync(run.OutputPath, CancellationToken.None);
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/empty-parent/empty-child" && e.IsDirectory);
        Assert.DoesNotContain(entries, e => e.Path.Contains("tool.exe", StringComparison.OrdinalIgnoreCase) || e.Path.Contains(".idea", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EmptyDirectoryRulesCannotAccidentallyRecurseIntoNewFiles()
    {
        Write("file.txt");
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        var run = await Run(Profile(OutputFormat.SevenZip), new InlineProgress(p =>
        {
            if (p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) Write("empty/new.txt", "created after scanning; must not be archived");
        }));
        Assert.True(run.Status == RunStatus.Completed, run.Message);
        var entries = await new SevenZip(new AppSettings().SevenZipPath).ListAsync(run.OutputPath, CancellationToken.None);
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/empty" && e.IsDirectory);
        Assert.DoesNotContain(entries, e => e.Path.EndsWith("new.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HashVerificationCatchesSourceChangesEvenWhenSizeAndTimestampMatch()
    {
        Write("file.txt", "first");
        string path = Path.Combine(Source, "file.txt");
        DateTime modified = File.GetLastWriteTimeUtc(path);
        var run = await Run(Profile(OutputFormat.SevenZip), new InlineProgress(p =>
        {
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            File.WriteAllText(path, "other");
            File.SetLastWriteTimeUtc(path, modified);
        }));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("SHA-256", run.Message);
        Assert.False(File.Exists(run.OutputPath));
        Assert.True(Directory.Exists(run.RetainedStagingPath));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(run.RetainedStagingPath, "view")));
    }

    [Fact]
    public async Task ReplacingASelectedFileWithADirectoryCannotIncludeItsContents()
    {
        Write("file.txt");
        var run = await Run(Profile(OutputFormat.SevenZip), new InlineProgress(p =>
        {
            if (!p.Operation.StartsWith("Compressing ", StringComparison.Ordinal)) return;
            string path = Path.Combine(Source, "file.txt");
            File.Delete(path);
            Directory.CreateDirectory(path);
            Write("file.txt/leak.txt", "must not be included");
        }));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.False(File.Exists(run.OutputPath));
        string partial = Assert.Single(Directory.GetFiles(Destination, "*.partial.7z"));
        var entries = await new SevenZip(new AppSettings().SevenZipPath).ListAsync(partial, CancellationToken.None);
        Assert.DoesNotContain(entries, e => e.Path.Contains("leak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAliasFallbackKeepsTheRootWithoutStagingSourceData()
    {
        Write("nested/file.txt", "fallback content");
        Write("snapshot/same-name.txt", "nested root name");
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        var profile = Profile(OutputFormat.SevenZip);
        var plan = await new BackupScanner().ScanAsync(profile);
        string work = Path.Combine(root, "metadata");
        Directory.CreateDirectory(work);
        string archivePath = Path.Combine(root, "fallback.7z");
        var logs = new List<string>();
        var warnings = new List<string>();
        var manifest = await new DirectArchiveBackup(new SevenZip(new AppSettings().SevenZipPath), allowAlias: false)
            .CreateAsync(profile, plan, Source, work, "snapshot", archivePath, warnings, logs.Add, CancellationToken.None);
        Assert.Equal(2, manifest.Count);
        Assert.Empty(warnings);
        Assert.Contains(logs, line => line.Contains("rewrites compressed archive data once", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(work, "view")));
        var entries = await new SevenZip(new AppSettings().SevenZipPath).ListAsync(archivePath, CancellationToken.None);
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/nested/file.txt");
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/snapshot/same-name.txt");
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/empty" && e.IsDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamedVerificationSupportsSolidAndMultipleNonSolidBlocks(bool solid)
    {
        var expected = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        for (int index = 0; index < 20; index++)
        {
            string relative = $"folder-{index % 3}/file-{index}.{(index % 2 == 0 ? "txt" : "bin")}";
            Write(relative, new string((char)('a' + index), index * 117));
            string full = Path.Combine(Source, relative);
            expected.Add(relative, new(relative, new FileInfo(full).Length, File.GetLastWriteTimeUtc(full), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)))));
        }
        string list = Path.Combine(root, "files.txt");
        await File.WriteAllLinesAsync(list, expected.Keys);
        string archivePath = Path.Combine(root, "blocks.7z");
        var start = new ProcessStartInfo(new AppSettings().SevenZipPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Source, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "a", "-t7z", "-mx1", "-scsUTF-8", "-spd", solid ? "-ms=1g" : "-ms=off", "-i@" + list, "--", archivePath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var reads = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync(); await reads;
        Assert.Equal(0, process.ExitCode);
        var archive = new SevenZip(new AppSettings().SevenZipPath);
        var entries = await archive.ListAsync(archivePath, CancellationToken.None);
        Assert.Equal(expected.Count, entries.Count);
        await archive.VerifyContentAsync(archivePath, entries, expected, _ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task DirectArchiveCanSkipUnreadableFilesDuringPreflight()
    {
        Write("locked.txt"); Write("available.txt");
        using var locked = new FileStream(Path.Combine(Source, "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var profile = Profile(OutputFormat.SevenZip);
        profile.ContinueOnErrors = true;
        var run = await Run(profile);
        Assert.True(run.Status == RunStatus.CompletedWithWarnings, run.Message);
        Assert.Equal(1, run.WarningCount);
        Assert.Equal(1, run.FilesCopied);
        var entries = await new SevenZip(new AppSettings().SevenZipPath).ListAsync(run.OutputPath, CancellationToken.None);
        Assert.Contains(entries, e => Glob.Normalize(e.Path) == "snapshot/available.txt");
        Assert.DoesNotContain(entries, e => e.Path.Contains("locked.txt", StringComparison.Ordinal));
    }
}
