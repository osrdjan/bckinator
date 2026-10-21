using System.Globalization;

namespace Bckinator.Core;

public sealed class SevenZip(string executable) : ArchiveTool(executable, "7-Zip")
{
    public static IReadOnlyList<string> CreateArguments(string archivePath, string fileList, string excludedContentsList, CompressionOptions options) =>
    [
        "a", "-t7z", $"-mx={options.Level}", $"-m0=LZMA2:d={options.DictionaryMb}m:fb={options.FastBytes}",
        $"-ms={options.SolidBlockGb}g", $"-mmt={options.Threads}", "-bsp1", "-bso1", "-bse1", "-sccUTF-8", "-scsUTF-8",
        "-spd", "-spm", "-r-", "-sse", "-ssp", "-y", "-i@" + fileList, "-xrw@" + excludedContentsList, "--", archivePath
    ];

    public override Task CreateAsync(string archivePath, string workingDirectory, string fileList, string excludedContentsList, BackupProfile profile, string rootName, Action<string> log, CancellationToken ct) =>
        CreateAsync(archivePath, workingDirectory, fileList, excludedContentsList, profile.Compression, log, ct);

    public Task CreateAsync(string archivePath, string workingDirectory, string fileList, string excludedContentsList, CompressionOptions options, Action<string> log, CancellationToken ct) =>
        RunAsync(CreateArguments(archivePath, fileList, excludedContentsList, options), workingDirectory, log, ct);

    public override Task RenameAsync(string archivePath, string renameList, Action<string> log, CancellationToken ct) =>
        RunAsync(["rn", "-spd", "-scsUTF-8", "-sccUTF-8", archivePath, "@" + renameList], Path.GetDirectoryName(archivePath)!, log, ct);

    public override Task VerifyAsync(string archivePath, Action<string> log, CancellationToken ct) =>
        RunAsync(["t", "-bsp0", "-bso1", "-bse1", "-sccUTF-8", "--", archivePath], Path.GetDirectoryName(archivePath)!, log, ct);

    public override async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string archivePath, CancellationToken ct)
    {
        var lines = new List<string>();
        await RunAsync(["l", "-slt", "-ba", "-sccUTF-8", "--", archivePath], Path.GetDirectoryName(archivePath)!, line => { lock (lines) lines.Add(line); }, ct);
        var entries = new List<ArchiveEntry>();
        string? path = null;
        long size = 0;
        bool directory = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (path is not null) entries.Add(new(path, directory, size));
                path = line[7..]; size = 0; directory = false;
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal)) size = long.Parse(line[7..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal)) directory = line[13..].Contains('D');
            else if (line == "Folder = +") directory = true;
        }
        if (path is not null) entries.Add(new(path, directory, size));
        return entries;
    }

    public override Task VerifyContentAsync(string archivePath, IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string, ManifestFile> expected, Action<string> log, CancellationToken ct) =>
        VerifyStreamAsync(["x", "-so", "-bso0", "-bsp0", "-bse1", "-sccUTF-8", "--", archivePath], archivePath, entries, expected, log, ct);
}
