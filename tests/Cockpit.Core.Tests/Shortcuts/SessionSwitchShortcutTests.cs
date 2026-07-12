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
    public void StaysActiveInTerminal_HoldsForTheSessionSwitchOnly()
    {
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.PreviousSession).Should().BeTrue();
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NextSession).Should().BeTrue();

        // Everything else must stand down over the terminal, or a plain gesture would be typed into the TUI.
        foreach (var descriptor in ShortcutCatalog.All)
        {
            if (descriptor.Action is ShortcutAction.PreviousSession or ShortcutAction.NextSession)
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
