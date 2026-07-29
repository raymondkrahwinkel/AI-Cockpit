using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The session switch is an ordinary, rebindable shortcut rather than a setting of its own. Two rules matter
/// and are easy to break silently: it ships bound to Ctrl+Shift+Up/Ctrl+Shift+Down (the bare Ctrl+arrows moved
/// to spatial pane focus, AC-31), and — unlike every other shortcut — it stays live while the operator types in
/// the embedded terminal, which is exactly when switching away is needed.
/// </summary>
public class SessionSwitchShortcutTests
{
    [Fact]
    public void Catalog_BindsTheSessionSwitchToCtrlShiftArrowByDefault()
    {
        Assert.Equal("Ctrl+Shift+Up", ShortcutCatalog.DefaultGesture(ShortcutAction.PreviousSession));
        Assert.Equal("Ctrl+Shift+Down", ShortcutCatalog.DefaultGesture(ShortcutAction.NextSession));
    }

    [Fact]
    public void Catalog_ListsTheSessionSwitchAsAnEditableAction()
    {
        Assert.Contains(ShortcutCatalog.All, descriptor => descriptor.Action == ShortcutAction.PreviousSession);
        Assert.Contains(ShortcutCatalog.All, descriptor => descriptor.Action == ShortcutAction.NextSession);
    }

    [Fact]
    public void StaysActiveInTerminal_HoldsForTheNavigationActions()
    {
        // The navigation shortcuts fire over a focused terminal (Raymond's call): switching session or
        // workspace, plus create and duplicate — the actions you reach for while driving a running TUI.
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousSession));
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextSession));
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousWorkspace));
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextWorkspace));
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NewSession));
        Assert.True(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.DuplicateSession));

        // The dialog-opening actions still stand down over the terminal, so a single-key shell binding reaches
        // the shell rather than being swallowed.
        var staysActive = new[]
        {
            ShortcutAction.PreviousSession, ShortcutAction.NextSession,
            ShortcutAction.PreviousWorkspace, ShortcutAction.NextWorkspace,
            ShortcutAction.NewSession, ShortcutAction.DuplicateSession,
        };

        foreach (var descriptor in ShortcutCatalog.All)
        {
            if (staysActive.Contains(descriptor.Action))
            {
                continue;
            }

            Assert.False(
                ShortcutCatalog.StaysActiveInTerminal(descriptor.Action),
                $"{descriptor.Action} would otherwise swallow a keystroke meant for the terminal");
        }
    }

    [Fact]
    public void Settings_CanRebindTheSessionSwitch()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.NextSession, "Alt+Right");

        Assert.Equal("Alt+Right", settings.GestureFor(ShortcutAction.NextSession));
    }

    [Fact]
    public void Settings_CanUnbindTheSessionSwitch()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.PreviousSession, string.Empty);

        Assert.Empty(settings.GestureFor(ShortcutAction.PreviousSession));
    }
}
