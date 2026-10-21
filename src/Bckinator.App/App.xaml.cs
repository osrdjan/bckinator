using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Bckinator.Core;

namespace Bckinator.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string? dataRoot = null;
            if (e.Args.Length > 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-dir" || !Path.IsPathFullyQualified(e.Args[1]))
                    throw new ArgumentException("Usage: Bckinator.App.exe [--data-dir <absolute-directory>]");
                dataRoot = Path.GetFullPath(e.Args[1]);
            }
            var store = new ProfileStore(dataRoot);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.Root.ToUpperInvariant())))[..24];
            instance = new Mutex(true, @"Local\Bckinator-" + key, out bool created);
            if (!created) { MessageBox.Show("Bckinator is already running for this data directory.", "bckinator"); Shutdown(); return; }
            var viewModel = new MainViewModel(store, new WindowsDialogs());
            await viewModel.InitializeAsync();
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Bckinator could not start. Existing configuration files have been preserved.\n\n" + ex.Message, "bckinator", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
