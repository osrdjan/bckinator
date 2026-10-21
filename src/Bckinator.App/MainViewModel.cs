using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using Bckinator.Core;

namespace Bckinator.App;

public sealed class MainViewModel : ObservableModel
{
    private readonly ProfileStore store;
    private readonly IDialogs dialogs;
    private readonly BackupScanner scanner = new();
    private readonly List<INotifyPropertyChanged> watched = [];
    private CancellationTokenSource? cancellation;
    private BackupProfile? selectedProfile;
    private BackupProfile draft = new();
    private string baseline = "", operation = "Ready", error = "", previewSummary = "Preview this profile to inspect file selection.", previewSearch = "", previewFilter = "All", preset = "Custom";
    private bool busy, indeterminate, logsExpanded;
    private bool storageBusy;
    private double percent;
    private int selectedTab;
    private int inputErrorCount;
    private SelectionRule? selectedRule;
    private DirectoryScope? selectedScope;
    private BackupRun? selectedRun;
    private IReadOnlyList<PlanEntry> entries = [];

    public MainViewModel(ProfileStore store, IDialogs dialogs)
    {
        this.store = store;
        this.dialogs = dialogs;
        NewProfile = new RelayCommand(New, () => IsIdle);
        SaveProfile = new AsyncCommand(() => StorageAsync(SaveAsync), ShowError, () => IsReady);
        DuplicateProfile = new RelayCommand(Duplicate, () => IsIdle);
        DeleteProfile = new AsyncCommand(() => StorageAsync(DeleteAsync), ShowError, () => IsIdle && SelectedProfile is not null);
        ImportProfile = new AsyncCommand(() => StorageAsync(ImportAsync), ShowError, () => IsIdle);
        ExportProfile = new AsyncCommand(() => StorageAsync(ExportAsync), ShowError, () => IsIdle);
        BrowseSource = new RelayCommand(() => { if (dialogs.PickFolder("Select source folder") is { } p) Draft.Source = p; }, () => IsIdle);
        BrowseDestination = new RelayCommand(() => { if (dialogs.PickFolder("Select backup destination") is { } p) Draft.Destination = p; }, () => IsIdle);
        BrowseSevenZip = new RelayCommand(() => { if (dialogs.OpenFile("Locate 7z.exe", "7-Zip executable|7z.exe|Executables|*.exe") is { } p) Settings.SevenZipPath = p; }, () => IsIdle);
        BrowseRar = new RelayCommand(() => { if (dialogs.OpenFile("Locate WinRAR Rar.exe", "WinRAR console executable|Rar.exe") is { } p) Settings.RarPath = p; }, () => IsIdle);
        UseRarDefaults = new RelayCommand(() => Draft.RarCompression.ResetToDefaults(), () => IsIdle && IsWinRar);
        SaveSettings = new AsyncCommand(() => StorageAsync(async () => { await store.WriteAsync("settings.json", Settings); Operation = "Settings saved"; }), ShowError, () => IsIdle);
        AddRule = new RelayCommand(() => { var r = new SelectionRule { Kind = RuleKind.FilePattern, Pattern = "*.tmp" }; Draft.Rules.Add(r); SelectedRule = r; }, () => IsIdle);
        RemoveRule = new RelayCommand(() => { if (SelectedRule is { } r) Draft.Rules.Remove(r); }, () => IsIdle && SelectedRule is not null);
        MoveRuleUp = new RelayCommand(() => MoveRule(-1), () => IsIdle && SelectedRule is not null && Draft.Rules.IndexOf(SelectedRule) > 0);
        MoveRuleDown = new RelayCommand(() => MoveRule(1), () => IsIdle && SelectedRule is not null && Draft.Rules.IndexOf(SelectedRule) < Draft.Rules.Count - 1);
        AddScope = new RelayCommand(() => { var s = new DirectoryScope { RelativeDirectory = "directory", IncludePatterns = "subfolder/\nfile.txt" }; Draft.Scopes.Add(s); SelectedScope = s; }, () => IsIdle);
        RemoveScope = new RelayCommand(() => { if (SelectedScope is { } s) Draft.Scopes.Remove(s); }, () => IsIdle && SelectedScope is not null);
        Preview = new AsyncCommand(PreviewAsync, ShowError, () => IsReady);
        StartBackup = new AsyncCommand(RunAsync, ShowError, () => IsReady);
        Cancel = new RelayCommand(() => { cancellation?.Cancel(); Operation = "Cancelling…"; }, () => IsBusy);
        OpenOutput = new RelayCommand(() => TryOpen(SelectedRun?.OutputPath), () => SelectedRun is not null);
        OpenLog = new RelayCommand(() => TryOpen(SelectedRun?.LogPath), () => SelectedRun is not null);
        OpenStaging = new RelayCommand(() => TryOpen(SelectedRun?.RetainedStagingPath), () => !string.IsNullOrEmpty(SelectedRun?.RetainedStagingPath));
        OpenAppData = new RelayCommand(() => { Directory.CreateDirectory(store.Root); TryOpen(store.Root); });
        Draft = ProfileStore.CreateSample();
    }

    public ObservableCollection<BackupProfile> Profiles { get; } = [];
    public ObservableCollection<BackupRun> History { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public AppSettings Settings { get; private set; } = new();
    public KeyValuePair<OutputFormat, string>[] OutputFormats { get; } =
        [new(OutputFormat.Folder, "Folder"), new(OutputFormat.SevenZip, "7-Zip (.7z)"), new(OutputFormat.WinRar, "WinRAR (.rar)")];
    public Array RarMethods { get; } = Enum.GetValues<RarCompressionMethod>();
    public int[] RarDictionaries { get; } = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];
    public Array RuleKinds { get; } = Enum.GetValues<RuleKind>();
    public Array RuleActions { get; } = Enum.GetValues<RuleAction>();
    public string[] PreviewFilters { get; } = ["All", "Included", "Excluded", "Warnings"];
    public string[] CompressionPresets { get; } = ["Custom", "Fast", "Balanced", "Maximum"];
    public string DataDirectory => store.Root;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy && !storageBusy;
    public bool IsReady => IsIdle && InputErrorCount == 0;
    public int InputErrorCount
    {
        get => inputErrorCount;
        set { Set(ref inputErrorCount, Math.Max(0, value)); CommandManager.InvalidateRequerySuggested(); }
    }
    public bool IsDirty => baseline != ProfileStore.Export(Draft);
    public string DraftState => IsDirty ? "Unsaved changes · runs use the settings shown" : "Profile saved";
    public string Operation { get => operation; private set => Set(ref operation, value); }
    public string Error { get => error; private set { Set(ref error, value); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string PreviewSummary { get => previewSummary; private set => Set(ref previewSummary, value); }
    public bool IsIndeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public double Percent { get => percent; private set => Set(ref percent, value); }
    public bool LogsExpanded { get => logsExpanded; set => Set(ref logsExpanded, value); }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    public SelectionRule? SelectedRule { get => selectedRule; set { Set(ref selectedRule, value); CommandManager.InvalidateRequerySuggested(); } }
    public DirectoryScope? SelectedScope { get => selectedScope; set { Set(ref selectedScope, value); Notify(nameof(HasSelectedScope)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasSelectedScope => SelectedScope is not null;
    public BackupRun? SelectedRun { get => selectedRun; set { Set(ref selectedRun, value); CommandManager.InvalidateRequerySuggested(); } }
    public ICollectionView PreviewItems { get; private set; } = CollectionViewSource.GetDefaultView(Array.Empty<PlanEntry>());
    public string PreviewSearch { get => previewSearch; set { Set(ref previewSearch, value); RefreshPreview(); } }
    public string PreviewFilter { get => previewFilter; set { Set(ref previewFilter, value); RefreshPreview(); } }
    public string NamingExample { get { try { return BackupNaming.Generate(Draft.NamingPattern, DateTimeOffset.Now) + (Draft.Format switch { OutputFormat.SevenZip => ".7z", OutputFormat.WinRar => ".rar", _ => "" }); } catch (Exception ex) { return ex.Message; } } }
    public bool IsSevenZip => Draft.Format == OutputFormat.SevenZip;
    public bool IsWinRar => Draft.Format == OutputFormat.WinRar;

    public string CompressionPreset
    {
        get => preset;
        set
        {
            Set(ref preset, value);
            if (value == "Custom") return;
            var c = Draft.Compression;
            (c.Level, c.DictionaryMb, c.FastBytes, c.SolidBlockGb, c.Threads) = value switch
            {
                "Fast" => (1, 8, 32, 1, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)),
                "Maximum" => (9, 64, 64, 4, Math.Clamp(Environment.ProcessorCount / 2, 1, 8)),
                _ => (5, 32, 32, 1, Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
            };
            preset = value;
            Notify();
        }
    }

    public BackupProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (ReferenceEquals(selectedProfile, value)) return;
            if (!IsIdle || !MayDiscard()) { Notify(); return; }
            selectedProfile = value;
            Notify();
            if (value is not null) LoadDraft(value);
        }
    }

    public BackupProfile Draft
    {
        get => draft;
        private set
        {
            Unwatch();
            draft = value;
            Watch();
            baseline = ProfileStore.Export(value);
            SelectedRule = null;
            SelectedScope = Draft.Scopes.FirstOrDefault();
            preset = "Custom";
            Notify(); Notify(nameof(CompressionPreset)); NotifyDraft();
        }
    }

    public ICommand NewProfile { get; }
    public ICommand SaveProfile { get; }
    public ICommand DuplicateProfile { get; }
    public ICommand DeleteProfile { get; }
    public ICommand ImportProfile { get; }
    public ICommand ExportProfile { get; }
    public ICommand BrowseSource { get; }
    public ICommand BrowseDestination { get; }
    public ICommand BrowseSevenZip { get; }
    public ICommand BrowseRar { get; }
    public ICommand UseRarDefaults { get; }
    public ICommand SaveSettings { get; }
    public ICommand AddRule { get; }
    public ICommand RemoveRule { get; }
    public ICommand MoveRuleUp { get; }
    public ICommand MoveRuleDown { get; }
    public ICommand AddScope { get; }
    public ICommand RemoveScope { get; }
    public ICommand Preview { get; }
    public ICommand StartBackup { get; }
    public ICommand Cancel { get; }
    public ICommand OpenOutput { get; }
    public ICommand OpenLog { get; }
    public ICommand OpenStaging { get; }
    public ICommand OpenAppData { get; }

    public async Task InitializeAsync()
    {
        var profiles = await store.ReadAsync<List<BackupProfile>>("profiles.json");
        if (profiles is null)
        {
            profiles = [ProfileStore.CreateSample()];
            await store.WriteAsync("profiles.json", profiles);
        }
        foreach (var profile in profiles) { ProfileValidator.ValidateDefinition(profile); Profiles.Add(profile); }
        Settings = await store.ReadAsync<AppSettings>("settings.json") ?? new();
        Notify(nameof(Settings));
        foreach (var run in (await store.ReadAsync<List<BackupRun>>("history.json") ?? []).OrderByDescending(r => r.StartedAt)) History.Add(run);
        selectedProfile = Profiles.FirstOrDefault();
        Notify(nameof(SelectedProfile));
        LoadDraft(selectedProfile ?? new BackupProfile());
    }

    public bool MayDiscard() => !IsDirty || dialogs.Confirm("Discard unsaved profile changes?");
    public void RequestCancellation() => cancellation?.Cancel();

    private void LoadDraft(BackupProfile profile)
    {
        Draft = ProfileStore.Clone(profile);
        entries = [];
        RefreshPreview();
        PreviewSummary = "Preview this profile to inspect file selection.";
        Error = "";
    }

    private void New()
    {
        if (!MayDiscard()) return;
        selectedProfile = null;
        Notify(nameof(SelectedProfile));
        LoadDraft(new());
        baseline = "";
        NotifyDraft();
        SelectedTab = 0;
    }

    private void Duplicate()
    {
        var copy = ProfileStore.Clone(Draft);
        copy.Id = Guid.NewGuid(); copy.Name += " (copy)";
        selectedProfile = null;
        Notify(nameof(SelectedProfile));
        LoadDraft(copy);
        baseline = "";
        NotifyDraft();
    }

    private async Task SaveAsync()
    {
        ProfileValidator.ValidateDefinition(Draft);
        var snapshot = ProfileStore.Clone(Draft);
        var saved = Profiles.Where(p => p.Id != snapshot.Id).Append(snapshot).ToList();
        await store.WriteAsync("profiles.json", saved);
        int index = Profiles.ToList().FindIndex(p => p.Id == snapshot.Id);
        // Avoid selection callbacks when replacing a list item.
        baseline = ProfileStore.Export(Draft);
        if (index >= 0) Profiles[index] = snapshot; else Profiles.Add(snapshot);
        selectedProfile = snapshot;
        Notify(nameof(SelectedProfile)); NotifyDraft();
        Operation = "Profile saved";
        Error = "";
    }

    private async Task StorageAsync(Func<Task> operation)
    {
        storageBusy = true; Notify(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested();
        try { await operation(); }
        finally { storageBusy = false; Notify(nameof(IsIdle)); Notify(nameof(SelectedProfile)); CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task DeleteAsync()
    {
        if (selectedProfile is null || !dialogs.Confirm($"Delete profile '{selectedProfile.Name}' and its unsaved edits? Existing backups and history are kept.")) return;
        var remaining = Profiles.Where(p => p.Id != selectedProfile.Id).ToList();
        await store.WriteAsync("profiles.json", remaining);
        baseline = ProfileStore.Export(Draft);
        var removed = selectedProfile;
        selectedProfile = null;
        Profiles.Remove(removed);
        selectedProfile = remaining.FirstOrDefault();
        Notify(nameof(SelectedProfile));
        LoadDraft(selectedProfile ?? new());
        Operation = "Profile deleted";
    }

    private async Task ImportAsync()
    {
        if (!MayDiscard()) return;
        string? path = dialogs.OpenFile("Import profile", "JSON profile|*.json");
        if (path is null) return;
        var profile = ProfileStore.Import(await File.ReadAllTextAsync(path));
        await store.WriteAsync("profiles.json", Profiles.Append(profile).ToList());
        baseline = ProfileStore.Export(Draft);
        Profiles.Add(profile);
        selectedProfile = profile;
        Notify(nameof(SelectedProfile));
        LoadDraft(profile);
        Operation = "Profile imported";
    }

    private async Task ExportAsync()
    {
        ProfileValidator.ValidateDefinition(Draft);
        string? path = dialogs.SaveFile("Export displayed profile", "backup-profile.json", "JSON profile|*.json");
        if (path is null) return;
        await File.WriteAllTextAsync(path, ProfileStore.Export(Draft));
        Operation = "Profile exported";
    }

    private void MoveRule(int offset)
    {
        if (SelectedRule is null) return;
        int index = Draft.Rules.IndexOf(SelectedRule);
        Draft.Rules.Move(index, index + offset);
    }

    private async Task PreviewAsync()
    {
        BeginOperation();
        SelectedTab = 2;
        try
        {
            var snapshot = ProfileStore.Clone(Draft);
            var plan = await scanner.ScanAsync(snapshot, MakeProgress(), cancellation!.Token);
            entries = plan.Entries;
            RefreshPreview();
            PreviewSummary = $"{plan.FileCount:N0} files included · {FormatBytes(plan.TotalBytes)} uncompressed · {plan.WarningCount} warnings";
            Operation = "Preview complete";
            AddLog(PreviewSummary);
        }
        catch (OperationCanceledException) { Operation = "Preview cancelled"; }
        finally { EndOperation(); }
    }

    private async Task RunAsync()
    {
        BeginOperation();
        try
        {
            var snapshot = ProfileStore.Clone(Draft);
            var settings = new AppSettings { SevenZipPath = Settings.SevenZipPath, RarPath = Settings.RarPath };
            // Run the orchestration off the UI thread, including filesystem metadata and hashing work.
            var progress = MakeProgress();
            var run = await Task.Run(() => new BackupExecutor(scanner).ExecuteAsync(snapshot, settings, store.RunsDirectory, progress, cancellation!.Token));
            History.Insert(0, run);
            SelectedRun = run;
            SelectedTab = 3;
            Operation = run.Status + ": " + run.Message;
            if (run.Status is RunStatus.Failed) Error = run.Message;
            try { await store.WriteAsync("history.json", History.ToList()); }
            catch (Exception ex) { ShowError(new IOException("The backup result is shown, but history could not be saved: " + ex.Message, ex)); }
        }
        finally { EndOperation(); }
    }

    private void BeginOperation()
    {
        Error = ""; Logs.Clear(); LogsExpanded = true;
        cancellation = new();
        IsBusy = true; IsIndeterminate = true; Percent = 0; Operation = "Starting…";
    }
    private void EndOperation()
    {
        IsBusy = false; IsIndeterminate = false;
        cancellation?.Dispose(); cancellation = null;
    }
    private IProgress<BackupProgress> MakeProgress() => new Progress<BackupProgress>(p =>
    {
        Operation = p.Operation;
        IsIndeterminate = p.Percent is null;
        if (p.Percent is { } value) Percent = value;
        if (p.Log is { } line) AddLog(line);
    });
    private void AddLog(string line)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (Logs.Count > 2000) Logs.RemoveAt(0);
    }
    private void RefreshPreview()
    {
        PreviewItems = CollectionViewSource.GetDefaultView(entries);
        PreviewItems.Filter = item => item is PlanEntry e &&
            (PreviewFilter == "All" || PreviewFilter == "Included" && e.Included || PreviewFilter == "Excluded" && !e.Included || PreviewFilter == "Warnings" && e.IsWarning) &&
            (PreviewSearch.Length == 0 || e.RelativePath.Contains(PreviewSearch, StringComparison.OrdinalIgnoreCase) || e.Reason.Contains(PreviewSearch, StringComparison.OrdinalIgnoreCase));
        PreviewItems.Refresh();
        Notify(nameof(PreviewItems));
    }
    private void Watch()
    {
        watched.Add(Draft); watched.Add(Draft.Compression); watched.Add(Draft.RarCompression);
        watched.AddRange(Draft.Rules); watched.AddRange(Draft.Scopes);
        foreach (var model in watched) model.PropertyChanged += ModelChanged;
        Draft.Rules.CollectionChanged += CollectionChanged;
        Draft.Scopes.CollectionChanged += CollectionChanged;
    }
    private void Unwatch()
    {
        foreach (var model in watched) model.PropertyChanged -= ModelChanged;
        watched.Clear();
        draft.Rules.CollectionChanged -= CollectionChanged;
        draft.Scopes.CollectionChanged -= CollectionChanged;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { Unwatch(); Watch(); InvalidateSelection(); }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Draft.Compression)) { preset = "Custom"; Notify(nameof(CompressionPreset)); }
        InvalidateSelection();
    }
    private void InvalidateSelection()
    {
        entries = []; RefreshPreview();
        PreviewSummary = "Settings changed. Run Preview again to refresh the selection.";
        NotifyDraft();
    }
    private void NotifyDraft() { Notify(nameof(IsDirty)); Notify(nameof(DraftState)); Notify(nameof(NamingExample)); Notify(nameof(IsSevenZip)); Notify(nameof(IsWinRar)); }
    private void ShowError(Exception ex) { Error = ex.Message; Operation = "Action failed"; AddLog(ex.Message); }
    private void TryOpen(string? path) { if (string.IsNullOrEmpty(path)) return; try { dialogs.OpenLocation(path); } catch (Exception ex) { ShowError(ex); } }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):N2} GiB" : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):N2} MiB" : bytes >= 1024 ? $"{bytes / 1024.0:N1} KiB" : $"{bytes:N0} bytes";
}
