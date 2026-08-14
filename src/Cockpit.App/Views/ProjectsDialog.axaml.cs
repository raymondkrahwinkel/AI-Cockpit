using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Managing the operator's projects, in a window of its own rather than a tab in Options (Raymond, 2026-07-24):
// a project is not a setting of the cockpit but the work it is pointed at, and where projects come from is about
// to widen beyond this machine.
public partial class ProjectsDialog : Window
{
    public ProjectsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, subtitle: "What your sessions work on: a folder, the profile that starts by default, and the servers they get.");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Selecting by clicking the row rather than through a ListBox: the row is a card, and a card that only
    // highlights when you hit a narrow strip of it reads as broken.
    // No button guard here, unlike the double-click below: Avalonia's Button marks a left-button PointerPressed as
    // handled, so this bubbling handler never sees a click that landed on one of the row's own buttons.
    private void OnProjectPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ProjectCardViewModel card } && DataContext is ProjectsViewModel projects)
        {
            projects.SelectedProject = card.Project;
        }
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_CameFromAButton(e.Source))
        {
            return;
        }

        if (sender is Control { DataContext: ProjectCardViewModel card } && DataContext is ProjectsViewModel projects)
        {
            _ = projects.EditAsync(card.Project);
        }
    }

    // The row carries buttons of its own since AC-772, and a Button handles Click but not DoubleTapped — so a
    // double-click on Start reached this handler as well and opened the editor on top of what the button had
    // started. A button's own action is the whole of what that click meant; the row's is for the rest of it.
    //
    // What this does not do is stop the second Click: double-clicking a button fires it twice here as it does
    // anywhere else, which is not something this row invented and not something it gets to change.
    private static bool _CameFromAButton(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) is not null;
}
