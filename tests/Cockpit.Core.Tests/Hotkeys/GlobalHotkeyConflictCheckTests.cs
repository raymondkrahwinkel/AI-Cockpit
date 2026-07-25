using FluentAssertions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Hotkeys;

namespace Cockpit.Core.Tests.Hotkeys;

/// <summary>
/// Two hotkeys on one key: the keyboard hook keys its map by key code and the portal leaves the choice to the
/// compositor, so one of the two features stops working. Without this the operator sets the key, something
/// silently stops, and nothing anywhere connects the two.
/// </summary>
public class GlobalHotkeyConflictCheckTests
{
    [Fact]
    public void KeysThatAreEachTheirOwn_AreNoClash()
    {
        var clash = GlobalHotkeyConflictCheck.Describe(
        [
            new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", "F9"),
            new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"),
        ]);

        clash.Should().BeNull();
    }

    [Fact]
    public void TwoFeaturesOnOneKey_AreNamedTogetherWithTheKey()
    {
        var clash = GlobalHotkeyConflictCheck.Describe(
        [
            new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", "F8"),
            new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"),
        ]);

        clash.Should().NotBeNull();
        clash.Should().Contain("Push to talk (hold)").And.Contain("Take a screenshot").And.Contain("F8");
    }

    /// <summary>
    /// The settings store the Avalonia key name, and nothing upstream forces its casing — a key typed as "f8"
    /// clashes with "F8" just as thoroughly, and would otherwise pass this check and fail at the desktop.
    /// </summary>
    [Fact]
    public void CasingDoesNotHideAClash()
    {
        var clash = GlobalHotkeyConflictCheck.Describe(
        [
            new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", "f8"),
            new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"),
        ]);

        clash.Should().NotBeNull();
    }

    /// <summary>A single armed key cannot clash with itself, and one switched-off feature leaves exactly that.</summary>
    [Fact]
    public void OneArmedKey_IsNoClash()
    {
        var clash = GlobalHotkeyConflictCheck.Describe(
            [new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8")]);

        clash.Should().BeNull();
    }
}
