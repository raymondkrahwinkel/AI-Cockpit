using Cockpit.Core.Tests.Voice;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// What the Options screen says about the two desktop-wide keys while they are being typed (AC-220). The clash
/// has to show <em>before</em> the save, because after it one of the two features has already silently stopped
/// working — which is the failure the warning exists to prevent, not to explain afterwards.
/// </summary>
public class ScreenshotOptionsTests
{
    [Fact]
    public void TwoKeysThatAreEachTheirOwn_ShowNoWarning()
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.VoiceEnabled = true;
        cockpit.VoiceGlobalPushToTalk = true;
        cockpit.VoicePushToTalkKeyName = "F9";
        cockpit.ScreenshotGlobalHotkeyEnabled = true;
        cockpit.ScreenshotHotkeyKeyName = "F8";

        Assert.Empty(cockpit.HotkeyConflict);
    }

    [Fact]
    public void SettingTheScreenshotKeyToThePushToTalkKey_WarnsImmediately()
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.VoiceEnabled = true;
        cockpit.VoiceGlobalPushToTalk = true;
        cockpit.VoicePushToTalkKeyName = "F9";
        cockpit.ScreenshotGlobalHotkeyEnabled = true;

        cockpit.ScreenshotHotkeyKeyName = "F9";

        Assert.NotEmpty(cockpit.HotkeyConflict);
        Assert.Contains("F9", cockpit.HotkeyConflict);
    }

    /// <summary>
    /// A key that is not armed cannot clash with anything: switching push-to-talk off frees its key, and warning
    /// about it would be warning about something that is not happening.
    /// </summary>
    [Fact]
    public void AKeyBelongingToASwitchedOffFeature_IsNoClash()
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.VoiceEnabled = true;
        cockpit.VoiceGlobalPushToTalk = false;
        cockpit.VoicePushToTalkKeyName = "F9";
        cockpit.ScreenshotGlobalHotkeyEnabled = true;
        cockpit.ScreenshotHotkeyKeyName = "F9";

        Assert.Empty(cockpit.HotkeyConflict);
    }

    /// <summary>Switching voice itself off frees push-to-talk's key too — the binding is only contributed when both are on.</summary>
    [Fact]
    public void WithVoiceOff_ThePushToTalkKeyIsNotClaimed()
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.VoiceEnabled = false;
        cockpit.VoiceGlobalPushToTalk = true;
        cockpit.VoicePushToTalkKeyName = "F9";
        cockpit.ScreenshotGlobalHotkeyEnabled = true;
        cockpit.ScreenshotHotkeyKeyName = "F9";

        Assert.Empty(cockpit.HotkeyConflict);
    }

    /// <summary>The warning has to clear again when the operator fixes it, or it reads as a state they cannot get out of.</summary>
    [Fact]
    public void ChangingTheKeyBack_ClearsTheWarning()
    {
        var cockpit = TestCockpit.NewViewModel();
        cockpit.VoiceEnabled = true;
        cockpit.VoiceGlobalPushToTalk = true;
        cockpit.VoicePushToTalkKeyName = "F9";
        cockpit.ScreenshotGlobalHotkeyEnabled = true;
        cockpit.ScreenshotHotkeyKeyName = "F9";

        cockpit.ScreenshotHotkeyKeyName = "F8";

        Assert.Empty(cockpit.HotkeyConflict);
    }
}
