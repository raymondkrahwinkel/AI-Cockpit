using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;

namespace Cockpit.App.Views;

/// <summary>
/// Managing the operator's projects, in a window of its own rather than a tab in Options (Raymond, 2026-07-24):
/// a project is not a setting of the cockpit but the work it is pointed at, and where projects come from is about
/// to widen beyond this machine.
/// </summary>
public partial class ProjectsDialog : Window
{
    public ProjectsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Selecting by clicking the row rather than through a ListBox: the row is a card, and a card that only
    // highlights when you hit a narrow strip of it reads as broken.
    private void OnProjectPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: Project project } && DataContext is ProjectsViewModel projects)
        {
            projects.SelectedProject = project;
        }
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: Project project } && DataContext is ProjectsViewModel projects)
        {
            _ = projects.EditAsync(project);
        }
    }
}
