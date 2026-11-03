using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The AC-54 read surface (<c>ICockpitSessionObserver.ActiveSessionUsage</c>) follows the selected session's own
/// usage and moves when it does. Here rather than in the unit tests because the observer marshals its events onto the UI thread, so without
/// a pumped dispatcher a change would be posted to a loop nobody runs and the assertion would race.
/// </summary>
[Collection("avalonia")]
public class PluginSessionUsageObserverTests
{
    /// <summary>A minimal concrete session — the base is abstract, and none of what this exercises needs a real driver.</summary>
    private sealed class TestSession : SessionPanelViewModel
    {
        protected override void OnVoiceTextReady(string text)
        {
        }

        public override Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

        protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng) => Task.FromResult<string?>(null);

        protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void ActiveSessionUsageChanged_Fires_WhenTheSelectedSessionsContextOrRateLimitsMove() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var session = new TestSession { ContextUsedPercent = 10 };
        cockpit.Sessions.Add(session);
        cockpit.SelectedSession = session;

        var observer = new PluginSessionObserver(cockpit);
        var fired = 0;
        observer.ActiveSessionUsageChanged += (_, _) => fired++;

        session.ContextUsedPercent = 80;

        Assert.True(fired > 0, "a ctx move on the selected session is a fresh usage story");
        Assert.Equal(80, observer.ActiveSessionUsage!.ContextUsedPercent);

        fired = 0;
        session.RateLimits.Add(new SessionRateWindow("5h", 20, ResetsAt: null));

        Assert.True(fired > 0, "a rate window appearing changes the usage snapshot");
    });

    [Fact]
    public void ActiveSessionUsageChanged_DoesNotFire_ForABackgroundSessionsChange() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var selected = new TestSession { ContextUsedPercent = 10 };
        var background = new TestSession { ContextUsedPercent = 10 };
        cockpit.Sessions.Add(selected);
        cockpit.Sessions.Add(background);
        cockpit.SelectedSession = selected;

        var observer = new PluginSessionObserver(cockpit);
        var fired = 0;
        observer.ActiveSessionUsageChanged += (_, _) => fired++;

        background.ContextUsedPercent = 90;

        Assert.Equal(0, fired);
    });
}
