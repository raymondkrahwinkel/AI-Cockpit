using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-2028: the runs behind the status bar's "Test runs: N", laid out like the delegated-tasks dialog.
public partial class TestRunsDialog : Window
{
    public TestRunsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnRunPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: TestRunRowViewModel run } && DataContext is TestRunsViewModel runs)
        {
            runs.SelectedRun = run;
        }
    }
}
