using Cockpit.Core.Shortcuts;
using FluentAssertions;

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
        ShortcutCatalog.DefaultGesture(ShortcutAction.PreviousSession).Should().Be("Ctrl+Shift+Up");
        ShortcutCatalog.DefaultGesture(ShortcutAction.NextSession).Should().Be("Ctrl+Shift+Down");
    }

    [Fact]
    public void Catalog_ListsTheSessionSwitchAsAnEditableAction()
    {
        ShortcutCatalog.All.Should().Contain(descriptor => descriptor.Action == ShortcutAction.PreviousSession)
            .And.Contain(descriptor => descriptor.Action == ShortcutAction.NextSession);
    }

    [Fact]
    public void StaysActiveInTerminal_HoldsForTheNavigationActions()
    {
        // The navigation shortcuts fire over a focused terminal (Raymond's call): switching session or
        // workspace, plus create and duplicate — the actions you reach for while driving a running TUI.
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousWorkspace).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextWorkspace).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NewSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.DuplicateSession).Should().BeTrue();

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

            ShortcutCatalog.StaysActiveInTerminal(descriptor.Action).Should().BeFalse(
                "{0} would otherwise swallow a keystroke meant for the terminal", descriptor.Action);
        }
    }

    [Fact]
    public void Settings_CanRebindTheSessionSwitch()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.NextSession, "Alt+Right");

        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Alt+Right");
    }

    [Fact]
    public void Settings_CanUnbindTheSessionSwitch()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.PreviousSession, string.Empty);

        settings.GestureFor(ShortcutAction.PreviousSession).Should().BeEmpty();
    }
}
