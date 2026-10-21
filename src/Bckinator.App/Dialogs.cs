using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Bckinator.App;

public interface IDialogs
{
    bool Confirm(string message);
    string? PickFolder(string title);
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string filename, string filter);
    void OpenLocation(string path);
}

public sealed class WindowsDialogs : IDialogs
{
    public bool Confirm(string message) => MessageBox.Show(message, "bckinator", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? SaveFile(string title, string filename, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = filename, Filter = filter, AddExtension = true, DefaultExt = ".json" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public void OpenLocation(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (File.Exists(path))
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select," + path);
            Process.Start(start);
        }
        else throw new FileNotFoundException("This location is no longer available.", path);
    }
}
