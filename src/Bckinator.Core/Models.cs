using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bckinator.Core;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public enum OutputFormat { Folder, SevenZip, WinRar }
public enum RarCompressionMethod { Store, Fastest, Fast, Normal, Good, Best }
public enum RuleAction { Exclude, Include }
public enum RuleKind { DirectoryName, FilePattern, RelativePath, PathGlob }
public enum RunStatus { Completed, CompletedWithWarnings, Failed, Cancelled }

public sealed class SelectionRule : ObservableModel
{
    private bool enabled = true;
    private RuleAction action;
    private RuleKind kind;
    private string pattern = "";
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public RuleAction Action { get => action; set => Set(ref action, value); }
    public RuleKind Kind { get => kind; set => Set(ref kind, value); }
    public string Pattern { get => pattern; set => Set(ref pattern, value); }
}

public sealed class DirectoryScope : ObservableModel
{
    private bool enabled = true;
    private string relativeDirectory = "";
    private string includePatterns = "";
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public string RelativeDirectory { get => relativeDirectory; set => Set(ref relativeDirectory, value); }
    // One literal relative file/directory or path glob per line. Literal directories include descendants.
    public string IncludePatterns { get => includePatterns; set => Set(ref includePatterns, value); }
    [JsonIgnore] public string[] Patterns => IncludePatterns?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

public sealed class CompressionOptions : ObservableModel
{
    private int level = 5, dictionaryMb = 32, fastBytes = 32, solidBlockGb = 1, threads = 2;
    public int Level { get => level; set => Set(ref level, value); }
    public int DictionaryMb { get => dictionaryMb; set => Set(ref dictionaryMb, value); }
    public int FastBytes { get => fastBytes; set => Set(ref fastBytes, value); }
    public int SolidBlockGb { get => solidBlockGb; set => Set(ref solidBlockGb, value); }
    public int Threads { get => threads; set => Set(ref threads, value); }
}

public sealed class RarCompressionOptions : ObservableModel
{
    private RarCompressionMethod method = RarCompressionMethod.Best;
    private int dictionaryMb = 32;
    private bool solid;
    public RarCompressionMethod Method { get => method; set => Set(ref method, value); }
    public int DictionaryMb { get => dictionaryMb; set => Set(ref dictionaryMb, value); }
    public bool Solid { get => solid; set => Set(ref solid, value); }
}

public sealed class BackupProfile : ObservableModel
{
    private string name = "New profile", description = "", source = "", destination = "", namingPattern = "backup_{yyyyMMdd_HHmmss}";
    private OutputFormat format = OutputFormat.SevenZip;
    private bool excludeLinks = true, continueOnErrors, includeOnly;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string Source { get => source; set => Set(ref source, value); }
    public string Destination { get => destination; set => Set(ref destination, value); }
    public string NamingPattern { get => namingPattern; set => Set(ref namingPattern, value); }
    public OutputFormat Format { get => format; set => Set(ref format, value); }
    public bool ExcludeLinks { get => excludeLinks; set => Set(ref excludeLinks, value); }
    public bool ContinueOnErrors { get => continueOnErrors; set => Set(ref continueOnErrors, value); }
    public bool IncludeOnly { get => includeOnly; set => Set(ref includeOnly, value); }
    public CompressionOptions Compression { get; set; } = new();
    public RarCompressionOptions RarCompression { get; set; } = new();
    public ObservableCollection<SelectionRule> Rules { get; set; } = [];
    public ObservableCollection<DirectoryScope> Scopes { get; set; } = [];
}

public sealed class AppSettings : ObservableModel
{
    private string sevenZipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
    public string SevenZipPath { get => sevenZipPath; set => Set(ref sevenZipPath, value); }
    private string rarPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "Rar.exe");
    public string RarPath { get => rarPath; set => Set(ref rarPath, value); }
}

public sealed record SelectionDecision(bool Included, bool Traverse, string Reason);
public sealed record PlanEntry(string RelativePath, bool IsDirectory, long Size, DateTime LastWriteUtc, bool Included, string Reason, bool IsWarning = false);
public sealed record BackupPlan(IReadOnlyList<PlanEntry> Entries)
{
    public int FileCount => Entries.Count(e => e.Included && !e.IsDirectory);
    public long TotalBytes => Entries.Where(e => e.Included && !e.IsDirectory).Sum(e => e.Size);
    public int WarningCount => Entries.Count(e => e.IsWarning);
}
public sealed record BackupProgress(string Operation, double? Percent = null, string? Log = null);
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    [JsonIgnore] public string DurationDisplay => $"{(int)Duration.TotalHours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}";
    public RunStatus Status { get; set; }
    public string OutputPath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string RetainedStagingPath { get; set; } = "";
    public long OutputBytes { get; set; }
    public int FilesCopied { get; set; }
    public int WarningCount { get; set; }
    public string Message { get; set; } = "";
}
public sealed record ManifestFile(string RelativePath, long Size, DateTime LastWriteUtc, string Sha256);
public sealed record BackupManifest(int SchemaVersion, Guid RunId, string ProfileName, DateTimeOffset CreatedAt, IReadOnlyList<ManifestFile> Files, IReadOnlyList<string> Warnings);
