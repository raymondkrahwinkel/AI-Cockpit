using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The AC-54 read surface (<c>ICockpitSessionObserver.ActiveSessionUsage</c>) mirrors the selected session's own
/// usage — the same ctx / 5h / wk figures the header pill renders, plus its profile label — and moves when they
/// do. Here rather than in the unit tests because the observer marshals its events onto the UI thread, so without
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

        protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void ActiveSessionUsage_MirrorsTheSelectedSessionsContextRateLimitsAndProfile() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var session = new TestSession { ActiveProfileLabel = "Work", ContextUsedPercent = 42 };
        session.RateLimits.Add(new SessionRateWindow("5h", 55, ResetsAt: null));
        session.RateLimits.Add(new SessionRateWindow("wk", 66, ResetsAt: null));
        cockpit.Sessions.Add(session);
        cockpit.SelectedSession = session;

        var observer = new PluginSessionObserver(cockpit);

        var usage = observer.ActiveSessionUsage;
        usage.Should().NotBeNull();
        usage!.ProfileLabel.Should().Be("Work");
        usage.ContextUsedPercent.Should().Be(42);
        usage.RateLimits.Select(window => (window.Label, window.UsedPercent))
            .Should().Equal(("5h", 55), ("wk", 66));
    });

    [Fact]
    public void ActiveSessionUsage_IsNull_WhenNothingIsSelected() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel { SelectedSession = null };
        var observer = new PluginSessionObserver(cockpit);

        observer.ActiveSessionUsage.Should().BeNull();
    });

    [Fact]
    public void ActiveSessionUsageChanged_Fires_WhenTheSelectedSessionsContextMoves() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var session = new TestSession { ContextUsedPercent = 10 };
        cockpit.Sessions.Add(session);
        cockpit.SelectedSession = session;

        var observer = new PluginSessionObserver(cockpit);
        var fired = 0;
        observer.ActiveSessionUsageChanged += (_, _) => fired++;

        session.ContextUsedPercent = 80;

        fired.Should().BeGreaterThan(0, "a ctx move on the selected session is a fresh usage story");
        observer.ActiveSessionUsage!.ContextUsedPercent.Should().Be(80);
    });

    [Fact]
    public void ActiveSessionUsageChanged_Fires_WhenTheSelectedSessionsRateLimitsChange() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var session = new TestSession { ContextUsedPercent = 10 };
        cockpit.Sessions.Add(session);
        cockpit.SelectedSession = session;

        var observer = new PluginSessionObserver(cockpit);
        var fired = 0;
        observer.ActiveSessionUsageChanged += (_, _) => fired++;

        session.RateLimits.Add(new SessionRateWindow("5h", 20, ResetsAt: null));

        fired.Should().BeGreaterThan(0, "a rate window appearing changes the usage snapshot");
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

        fired.Should().Be(0, "only the selected session feeds the active-usage surface");
    });
}
