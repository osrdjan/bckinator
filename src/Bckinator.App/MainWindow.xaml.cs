using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Bckinator.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private void ValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.InputErrorCount += e.Action == ValidationErrorEventAction.Added ? 1 : -1;
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsIdle)
        {
            MessageBox.Show(this, vm.IsBusy ? "Use Cancel and wait for the operation to stop before closing." : "Wait for the settings operation to finish before closing.", "bckinator", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
        }
        else if (!vm.MayDiscard()) e.Cancel = true;
    }
}
