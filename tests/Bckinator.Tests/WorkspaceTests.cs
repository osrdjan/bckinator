using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bckinator.App;
using Bckinator.Core;

namespace Bckinator.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public Task WorkspaceLoadsAllTabsAndSavesPreviewsAndRunsThroughBoundCommands() => OnSta(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "Bckinator.Tests", Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        try
        {
            var application = new Bckinator.App.App();
            application.InitializeComponent();
            var store = new ProfileStore(Path.Combine(root, "config"));
            var dialogs = new TestDialogs();
            var vm = new MainViewModel(store, dialogs);
            await vm.InitializeAsync();
            window = new MainWindow { DataContext = vm };
            window.Measure(new Size(1180, 840)); window.Arrange(new Rect(0, 0, 1180, 840)); window.UpdateLayout();
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            Assert.NotNull(vm.SelectedScope);
            Assert.Contains("diagrams/", vm.SelectedScope.IncludePatterns);
            vm.NewProfile.Execute(null);
            tabs.SelectedIndex = 0; window.UpdateLayout();
            ((TextBox)window.FindName("ProfileName")).Text = "Workspace test";
            Assert.Equal("Workspace test", vm.Draft.Name);
            Assert.True(vm.IsDirty);
            Directory.CreateDirectory(Path.Combine(root, "source"));
            await File.WriteAllTextAsync(Path.Combine(root, "source", "file.txt"), "UI command backup");
            vm.Draft.Source = Path.Combine(root, "source");
            vm.Draft.Destination = Path.Combine(root, "backups");
            vm.Draft.Format = OutputFormat.Folder;
            vm.Draft.NamingPattern = "ui_snapshot";
            await Execute(vm.SaveProfile);
            Assert.Empty(vm.Error);
            Assert.False(vm.IsDirty);
            Assert.Equal(2, (await store.ReadAsync<BackupProfile[]>("profiles.json"))!.Length);
            Guid savedId = vm.Draft.Id;
            var savedSelection = vm.SelectedProfile;
            vm.Draft.Name = "Unsaved edit";
            dialogs.ConfirmResponse = false;
            var list = (ListBox)window.FindName("ProfileList");
            list.SelectedItem = vm.Profiles.First(p => p.Id != savedId);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(savedSelection, vm.SelectedProfile);
            Assert.Same(savedSelection, list.SelectedItem);
            Assert.Equal("Unsaved edit", vm.Draft.Name);
            dialogs.ConfirmResponse = true;
            vm.Draft.Name = "Workspace test";
            vm.DuplicateProfile.Execute(null);
            Assert.True(vm.IsDirty);
            Assert.NotEqual(savedId, vm.Draft.Id);
            dialogs.SavePath = Path.Combine(root, "export.json");
            await Execute(vm.ExportProfile);
            Assert.True(File.Exists(dialogs.SavePath));
            dialogs.OpenPath = dialogs.SavePath;
            await Execute(vm.ImportProfile);
            Assert.Empty(vm.Error);
            Assert.Equal(3, vm.Profiles.Count);
            Assert.False(vm.IsDirty);
            await Execute(vm.DeleteProfile);
            Assert.Equal(2, vm.Profiles.Count);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Id == savedId);
            await Execute(vm.Preview);
            Assert.Empty(vm.Error);
            Assert.Single(vm.PreviewItems.Cast<PlanEntry>(), e => e.Included);
            vm.Draft.Rules.Add(new() { Kind = RuleKind.FilePattern, Pattern = "*.txt" });
            Assert.Empty(vm.PreviewItems.Cast<PlanEntry>());
            vm.Draft.Rules.Clear();
            await Execute(vm.StartBackup);
            Assert.Empty(vm.Error);
            Assert.Equal(RunStatus.Completed, Assert.Single(vm.History).Status);
            Assert.False(vm.IsBusy);
            Assert.Equal("UI command backup", await File.ReadAllTextAsync(Path.Combine(root, "backups", "ui_snapshot", "file.txt")));
            vm.InputErrorCount = 1;
            Assert.False(vm.StartBackup.CanExecute(null));
            Assert.False(vm.SaveProfile.CanExecute(null));
            vm.InputErrorCount = 0;
            Assert.DoesNotContain("System.Windows.Data Error", errors.Text);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            // The model is test-owned; avoid discard prompts during teardown.
            if (window is not null) { window.DataContext = null; window.Close(); }
            if (!ProfileValidator.IsSameOrBelow(root, Path.Combine(Path.GetTempPath(), "Bckinator.Tests"))) throw new InvalidOperationException("Test cleanup boundary failed.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    });

    private static async Task Execute(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!command.CanExecute(null)) await Task.Delay(10, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    private sealed class TestDialogs : IDialogs
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public bool ConfirmResponse { get; set; } = true;
        public bool Confirm(string message) => ConfirmResponse;
        public string? PickFolder(string title) => null;
        public string? OpenFile(string title, string filter) => OpenPath;
        public string? SaveFile(string title, string filename, string filter) => SavePath;
        public void OpenLocation(string path) { }
    }
    private sealed class BindingErrors : TraceListener
    {
        public string Text { get; private set; } = "";
        public override void Write(string? message) => Text += message;
        public override void WriteLine(string? message) => Text += message + Environment.NewLine;
    }
}
