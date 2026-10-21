using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bckinator.Core;

public sealed class SevenZip(string executable)
{
    public static IReadOnlyList<string> CreateArguments(string archivePath, string fileList, string excludedContentsList, CompressionOptions options) =>
    [
        "a", "-t7z", $"-mx={options.Level}", $"-m0=LZMA2:d={options.DictionaryMb}m:fb={options.FastBytes}",
        $"-ms={options.SolidBlockGb}g", $"-mmt={options.Threads}", "-bsp1", "-bso1", "-bse1", "-sccUTF-8", "-scsUTF-8",
        "-spd", "-spm", "-r-", "-sse", "-ssp", "-y", "-i@" + fileList, "-xrw@" + excludedContentsList, "--", archivePath
    ];

    public Task CreateAsync(string archivePath, string workingDirectory, string fileList, string excludedContentsList, CompressionOptions options, Action<string> log, CancellationToken ct) =>
        RunAsync(CreateArguments(archivePath, fileList, excludedContentsList, options), workingDirectory, log, ct);

    public Task RenameAsync(string archivePath, string renameList, Action<string> log, CancellationToken ct) =>
        RunAsync(["rn", "-spd", "-scsUTF-8", "-sccUTF-8", archivePath, "@" + renameList], Path.GetDirectoryName(archivePath)!, log, ct);

    public Task VerifyAsync(string archivePath, Action<string> log, CancellationToken ct) =>
        RunAsync(["t", "-bsp0", "-bso1", "-bse1", "-sccUTF-8", "--", archivePath], Path.GetDirectoryName(archivePath)!, log, ct);

    public async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string archivePath, CancellationToken ct)
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

    // All extraction output stays in this pipe. No extracted files are written to disk.
    public async Task VerifyContentAsync(string archivePath, IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string, ManifestFile> expected, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(["x", "-so", "-bso0", "-bsp0", "-bse1", "-sccUTF-8", "--", archivePath], Path.GetDirectoryName(archivePath)!);
        using var cancel = RegisterCancellation(process, ct);
        var stderr = DrainAsync(process.StandardError, log);
        try
        {
            byte[] buffer = new byte[131072];
            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var file = expected[Glob.Normalize(entry.Path)];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long remaining = entry.Size;
                while (remaining > 0)
                {
                    int read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                    if (read == 0) throw new IOException($"Archive content ended early: {entry.Path}");
                    hash.AppendData(buffer, 0, read); remaining -= read;
                }
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(file.Sha256, StringComparison.Ordinal))
                    throw new IOException($"Archived content differs from the preflight SHA-256 hash: {file.RelativePath}. The source may have changed while compressing.");
            }
            if (await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
                throw new IOException("The archive contains unexpected file data.");
            await process.WaitForExitAsync(ct);
            await stderr;
            ct.ThrowIfCancellationRequested();
            CheckExitCode(process);
        }
        finally { await StopAsync(process); await stderr; }
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(arguments, workingDirectory);
        using var cancel = RegisterCancellation(process, ct);
        var readers = Task.WhenAll(DrainAsync(process.StandardOutput, log), DrainAsync(process.StandardError, log));
        try
        {
            await process.WaitForExitAsync(ct);
            await readers;
            ct.ThrowIfCancellationRequested();
            CheckExitCode(process);
        }
        finally { await StopAsync(process); await readers; }
    }

    private Process Start(IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("7-Zip was not found. Set the path to 7z.exe in Settings.", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start 7-Zip.");
        return process;
    }

    private static CancellationTokenRegistration RegisterCancellation(Process process, CancellationToken ct) => ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

    private static async Task DrainAsync(StreamReader reader, Action<string> log)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) log(line);
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static void CheckExitCode(Process process)
    {
        if (process.ExitCode != 0) throw new IOException($"7-Zip returned exit code {process.ExitCode}. The backup was not finalized; see the run log.");
    }
}

public sealed record ArchiveEntry(string Path, bool IsDirectory, long Size);
