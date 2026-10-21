using System.Diagnostics;

namespace Bckinator.Core;

public sealed class SevenZip(string executable)
{
    public static IReadOnlyList<string> CreateArguments(string archivePath, string rootFolderName, CompressionOptions options) =>
    [
        "a", "-t7z", $"-mx={options.Level}", $"-m0=LZMA2:d={options.DictionaryMb}m:fb={options.FastBytes}",
        $"-ms={options.SolidBlockGb}g", $"-mmt={options.Threads}", "-bsp1", "-bso1", "-bse1", "-y", "--", archivePath, rootFolderName
    ];

    public async Task CreateAsync(string archivePath, string stagingDirectory, string rootName, CompressionOptions options, Action<string> log, CancellationToken ct)
    {
        await RunAsync(CreateArguments(archivePath, rootName, options), stagingDirectory, log, ct);
    }

    public Task VerifyAsync(string archivePath, Action<string> log, CancellationToken ct) =>
        RunAsync(["t", "-bsp0", "-bso1", "-bse1", "--", archivePath], Path.GetDirectoryName(archivePath)!, log, ct);

    private async Task RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("7-Zip was not found. Set the path to 7z.exe in Settings.", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start 7-Zip.");
        using var cancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) log(line.Trim());
        }
        var readers = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try
        {
            await process.WaitForExitAsync(ct);
            await readers;
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"7-Zip returned exit code {process.ExitCode}. The backup was not finalized; see the run log.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await readers;
        }
    }
}
