using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The task view on delegated work (#67): the list of tasks other sessions handed to a profile, what each one
/// produced, and a stop button while it is still running. <see cref="Window.DataContext"/> is the shared
/// <see cref="DelegatedTasksViewModel"/>, so it shows the same tasks the orchestrator's own tools report on.
/// </summary>
public partial class DelegatedTasksDialog : Window
{
    public DelegatedTasksDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Whole-row click selects the task, like the session sidebar: the clicked task is the row's DataContext
    // rather than a bindable CommandParameter.
    private void OnTaskPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: DelegatedTaskRowViewModel task } && DataContext is DelegatedTasksViewModel tasks)
        {
            tasks.SelectedTask = task;
        }
    }
}
