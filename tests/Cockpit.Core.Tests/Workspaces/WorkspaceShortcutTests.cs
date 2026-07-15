using Cockpit.Core.Shortcuts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// The workspace switch shortcuts (Raymond, 2026-07-15: "CTRL+SHIFT+Arrow Left/Right … dus 2 shortcuts voor
/// heen en weer"). These pin the properties that would otherwise break silently: the gestures themselves, the
/// axis split against the session switch, and staying live over the terminal.
/// </summary>
public class WorkspaceShortcutTests
{
    [Theory]
    [InlineData(ShortcutAction.PreviousWorkspace, "Ctrl+Shift+Left")]
    [InlineData(ShortcutAction.NextWorkspace, "Ctrl+Shift+Right")]
    public void WorkspaceSwitch_DefaultsToCtrlShiftArrow(ShortcutAction action, string expected)
    {
        ShortcutCatalog.DefaultGesture(action).Should().Be(expected);
    }

    [Fact]
    public void WorkspaceSwitch_DoesNotCollideWithTheSessionSwitch_WhichOwnsTheVerticalAxis()
    {
        // The two switches are deliberately split by axis: sessions step a vertical sidebar, workspaces step a
        // horizontal tab strip. Rebinding one onto the other's gesture is the operator's business, but the
        // shipped defaults must not overlap.
        var gestures = ShortcutCatalog.All.Select(descriptor => descriptor.DefaultGesture).Where(gesture => gesture.Length > 0);

        gestures.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(ShortcutAction.PreviousWorkspace)]
    [InlineData(ShortcutAction.NextWorkspace)]
    public void WorkspaceSwitch_StaysActiveInTheTerminal_SinceThatIsWhereYouSwitchFrom(ShortcutAction action)
    {
        ShortcutCatalog.StaysActiveInTerminal(action).Should().BeTrue();
    }

    [Fact]
    public void EveryAction_IsInTheCatalogExactlyOnce_SoNoneCanBeAddedWithoutALabelAndDefault()
    {
        var actions = Enum.GetValues<ShortcutAction>();

        ShortcutCatalog.All.Select(descriptor => descriptor.Action).Should().BeEquivalentTo(actions);
    }
}
