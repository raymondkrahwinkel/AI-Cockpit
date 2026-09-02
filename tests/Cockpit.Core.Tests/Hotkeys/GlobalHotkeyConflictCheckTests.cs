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
    /// <summary>
    /// Two features on one key are named together with the key. The lower-case row is the one that would
    /// otherwise pass this check and fail at the desktop: the settings store the Avalonia key name and nothing
    /// upstream forces its casing, so "f8" clashes with "F8" just as thoroughly.
    /// </summary>
    [Theory]
    [InlineData("F8")]
    [InlineData("f8")]
    public void TwoFeaturesOnOneKey_AreNamedTogetherWithTheKey_WhateverTheCasing(string pushToTalkKey)
    {
        var clash = GlobalHotkeyConflictCheck.Describe(
        [
            new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", pushToTalkKey),
            new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"),
        ]);

        Assert.NotNull(clash);
        Assert.Contains("Push to talk (hold)", clash);
        Assert.Contains("Take a screenshot", clash);
        // Case-insensitively: the sentence names the key as the first binding spells it, so a clash found through
        // the lower-case spelling reports that one.
        Assert.Contains("F8", clash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keys that are each their own are no clash — and neither is a single armed key, which is what one
    /// switched-off feature leaves behind and which cannot clash with itself.
    /// </summary>
    [Fact]
    public void KeysThatAreEachTheirOwn_AreNoClash()
    {
        Assert.Null(GlobalHotkeyConflictCheck.Describe(
        [
            new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", "F9"),
            new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"),
        ]));

        Assert.Null(GlobalHotkeyConflictCheck.Describe(
            [new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8")]));
    }
}
