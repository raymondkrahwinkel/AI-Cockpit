using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// The managed-worktrees dialog (AC-85): the git worktrees the cockpit created, each one's git state and whether its
/// session is still alive, with reattach for a gone one and a remove that asks before it can lose unsaved work.
/// </summary>
public partial class WorktreesDialog : Window
{
    public WorktreesDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
