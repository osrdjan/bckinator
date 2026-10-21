using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Bckinator.Core;

public abstract class ArchiveTool(string executable, string name)
{
    public string Name { get; } = name;
    public virtual bool NativeRootPrefix => false;
    public abstract Task CreateAsync(string archivePath, string workingDirectory, string fileList, string excludedContentsList, BackupProfile profile, string rootName, Action<string> log, CancellationToken ct);
    public virtual Task RenameAsync(string archivePath, string renameList, Action<string> log, CancellationToken ct) => throw new NotSupportedException();
    public abstract Task VerifyAsync(string archivePath, Action<string> log, CancellationToken ct);
    public abstract Task<IReadOnlyList<ArchiveEntry>> ListAsync(string archivePath, CancellationToken ct);
    public abstract Task VerifyContentAsync(string archivePath, IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string, ManifestFile> expected, Action<string> log, CancellationToken ct);

    // All extraction output stays in this pipe. No extracted files are written to disk.
    protected async Task VerifyStreamAsync(IReadOnlyList<string> arguments, string archivePath, IReadOnlyList<ArchiveEntry> entries, IReadOnlyDictionary<string, ManifestFile> expected, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(arguments, Path.GetDirectoryName(archivePath)!);
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

    protected async Task RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> log, CancellationToken ct)
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
        if (!File.Exists(executable)) throw new FileNotFoundException($"{Name} was not found. Set its executable path in Settings.", executable);
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
        if (!process.Start()) throw new IOException($"Could not start {Name}.");
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

    private void CheckExitCode(Process process)
    {
        if (process.ExitCode != 0) throw new IOException($"{Name} returned exit code {process.ExitCode}. The backup was not finalized; see the run log.");
    }
}

public sealed record ArchiveEntry(string Path, bool IsDirectory, long Size);
