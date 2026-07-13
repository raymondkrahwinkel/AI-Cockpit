using Cockpit.Core.Shortcuts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The session switch is an ordinary, rebindable shortcut rather than a setting of its own. Two rules matter
/// and are easy to break silently: it ships bound to Ctrl+Up/Ctrl+Down, and — unlike every other shortcut — it
/// stays live while the operator types in the embedded terminal, which is exactly when switching away is needed.
/// </summary>
public class SessionSwitchShortcutTests
{
    [Fact]
    public void Catalog_BindsTheSessionSwitchToCtrlArrowByDefault()
    {
        ShortcutCatalog.DefaultGesture(ShortcutAction.PreviousSession).Should().Be("Ctrl+Up");
        ShortcutCatalog.DefaultGesture(ShortcutAction.NextSession).Should().Be("Ctrl+Down");
    }

    [Fact]
    public void Catalog_ListsTheSessionSwitchAsAnEditableAction()
    {
        ShortcutCatalog.All.Should().Contain(descriptor => descriptor.Action == ShortcutAction.PreviousSession)
            .And.Contain(descriptor => descriptor.Action == ShortcutAction.NextSession);
    }

    [Fact]
    public void StaysActiveInTerminal_HoldsForTheSessionManagementActions()
    {
        // The session-management shortcuts fire over a focused terminal (Raymond's call): switch, plus
        // create and duplicate a session — the actions you reach for while driving a running TUI.
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NewSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.DuplicateSession).Should().BeTrue();

        // The dialog-opening actions still stand down over the terminal, so a single-key shell binding reaches
        // the shell rather than being swallowed.
        var staysActive = new[]
        {
            ShortcutAction.PreviousSession, ShortcutAction.NextSession,
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
