using System.Globalization;

namespace Bckinator.Core;

public sealed class WinRar(string executable) : ArchiveTool(executable, "WinRAR")
{
    public override bool NativeRootPrefix => true;

    public static IReadOnlyList<string> CreateArguments(string archivePath, string fileList, string rootName, RarCompressionOptions options) =>
    [
        "a", "-cfg-", $"-m{(int)options.Method}", $"-md{options.DictionaryMb}m", options.Solid ? "-s" : "-s-",
        "-r-", "-scflr", "-idcp", "-y", "-p-", "-@+", "-ap" + rootName, "--", archivePath, "@" + fileList
    ];

    public override Task CreateAsync(string archivePath, string workingDirectory, string fileList, string excludedContentsList, BackupProfile profile, string rootName, Action<string> log, CancellationToken ct) =>
        // -r- stores explicitly listed directories as empty entries, without reading their children.
        RunAsync(CreateArguments(archivePath, fileList, rootName, profile.RarCompression), workingDirectory, log, ct);

    public override Task VerifyAsync(string archivePath, Action<string> log, CancellationToken ct) =>
        RunAsync(["t", "-cfg-", "-scfr", "-idcp", "-p-", "-y", "--", archivePath], Path.GetDirectoryName(archivePath)!, log, ct);

    public override async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string archivePath, CancellationToken ct)
    {
        var lines = new List<string>();
        await RunAsync(["lt", "-cfg-", "-scfr", "-idc", "-p-", "--", archivePath], Path.GetDirectoryName(archivePath)!, line => { lock (lines) lines.Add(line); }, ct);
        var entries = new List<ArchiveEntry>();
        string? path = null;
        long size = 0;
        bool directory = false;
        foreach (string line in lines)
        {
            // Keep whitespace in the name itself. Only the fixed label is stripped.
            string field = line.TrimStart();
            if (field.StartsWith("Name: ", StringComparison.Ordinal))
            {
                if (path is not null) entries.Add(new(path, directory, size));
                path = field[6..]; size = 0; directory = false;
            }
            else if (field.StartsWith("Size: ", StringComparison.Ordinal)) size = long.Parse(field[6..], CultureInfo.InvariantCulture);
            else if (field == "Type: Directory") directory = true;
        }
        if (path is not null) entries.Add(new(path, directory, size));
        return entries;
    }

    public override Task VerifyContentAsync(string archivePath, IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string, ManifestFile> expected, Action<string> log, CancellationToken ct) =>
        VerifyStreamAsync(["p", "-cfg-", "-scfr", "-idq", "-p-", "-y", "--", archivePath], archivePath, entries, expected, log, ct);
}
