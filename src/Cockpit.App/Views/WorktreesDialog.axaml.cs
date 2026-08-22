using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Help;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// The managed-worktrees dialog (AC-85): the git worktrees the cockpit created, each one's git state and whether its
// session is still alive, with reattach for a gone one and a remove that asks before it can lose unsaved work.
public partial class WorktreesDialog : Window
{
    public WorktreesDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);

        // AC-1040: the page that explains what a row's state means, from the panel showing the states. Hides
        // itself when the documentation is not there.
        if (Program.Services?.GetService<HelpService>() is { } help)
        {
            IntroHelp.Children.Add(new HelpHint(help, new HelpAddress("worktrees", "the-panel"), origin: "a “?” in Managed worktrees"));
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Opens the worktree folder in the operating system's own file manager, so the operator can look at the files
    // directly. UseShellExecute hands the path to the OS handler — Explorer, Finder, or xdg-open on Linux — so it
    // stays cross-platform. Read-only and offered for any row; a folder that is gone simply does nothing.
    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ManagedWorktreeRowViewModel row } && Directory.Exists(row.WorktreePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(row.WorktreePath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No handler to open a folder (a headless or unusual environment) — better to do nothing than crash.
            }
        }
    }
}
