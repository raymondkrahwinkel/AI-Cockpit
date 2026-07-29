using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Saving global push-to-talk offers a restart only where it is actually needed: on Linux the hotkey is a
/// desktop-portal binding the compositor takes at startup, so a change applies on next launch; on Windows/macOS
/// the coordinator re-arms it live. And only for a real change — toggling it and back leaves nothing to restart
/// for. The platform gate is a hard runtime check on the view model, so the decision is pulled out to be testable
/// on any OS.
/// </summary>
public class GlobalPushToTalkRestartTests
{
    [Theory]
    [InlineData(false, true)]   // off → on
    [InlineData(true, false)]   // on → off
    public void OnLinux_WhenTheSavedValueDiffersFromWhatIsRunning_OffersARestart(bool running, bool saved)
    {
        Assert.True(CockpitViewModel.ShouldOfferGlobalPushToTalkRestart(isLinux: true, running, saved));
    }

    [Fact]
    public void OnLinux_WhenTheSavedValueMatchesWhatIsRunning_OffersNoRestart()
    {
        // Toggled and back, or saved without changing it — the running hotkey already matches.
        Assert.False(CockpitViewModel.ShouldOfferGlobalPushToTalkRestart(isLinux: true, runningValue: true, savedValue: true));
    }

    [Fact]
    public void OffLinux_NeverOffersARestart_BecauseTheChangeAppliesLive()
    {
        Assert.False(CockpitViewModel.ShouldOfferGlobalPushToTalkRestart(isLinux: false, runningValue: false, savedValue: true));
    }

    [Fact]
    public void BeforeTheRunningValueIsKnown_OffersNoRestart()
    {
        // Null baseline: settings were never loaded, so there is no startup value to have diverged from.
        Assert.False(CockpitViewModel.ShouldOfferGlobalPushToTalkRestart(isLinux: true, runningValue: null, savedValue: true));
    }
}
