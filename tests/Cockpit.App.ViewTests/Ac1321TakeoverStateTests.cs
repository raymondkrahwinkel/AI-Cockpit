using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1321: while a paired controller holds the line, the node's own assistant stands down and its screen says so —
// measured on the full CockpitView in the Simple stand, fed by the real presence on a clock the test turns.
[Collection("avalonia")]
public class Ac1321TakeoverStateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // Criterion 1: the notice names the machine and offers no button, and a local turn does not start; without a
    // controller the same node behaves as it did.
    [Fact]
    public async Task WithAnActiveController_TheNoticeNamesTheMachine_AndNoLocalTurnStarts() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen-empty");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var presence = new NodeControllerPresence(new FakeTimeProvider(Noon));
            cockpit.WatchController(presence);
            var host = _Host(cockpit);
            await host.ApplySettingsAsync();
            window.UpdateLayout();

            Assert.Null(_Notice(window));
            await host.SendAsync("hello");
            Assert.DoesNotContain("LAPTOP", host.UnavailableReason ?? string.Empty);

            presence.Seen("LAPTOP");
            window.UpdateLayout();

            var notice = _Notice(window);
            Assert.NotNull(notice);
            Assert.Contains("LAPTOP", notice.Text);
            Assert.Contains(notice.GetVisualAncestors(), a => a is ScrollViewer { Name: "StartOffer" });
            Assert.False(_Named<Button>(window, "OpenAssistantOptionsButton").IsEffectivelyVisible, "nothing here ends a takeover, so no button");

            await host.SendAsync("hello");
            Assert.Null(host.Session);
            Assert.Equal(AssistantActivity.Unavailable, host.Activity);
            Assert.Contains("LAPTOP", host.UnavailableReason);
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 2: the controller falls silent, and within the window the assistant is back and the notice gone —
    // on the clock, not on a wait, and not one poll early.
    [Fact]
    public async Task WhenTheControllerFallsSilent_TheAssistantIsBackWithinTheWindow() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen-empty");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var clock = new FakeTimeProvider(Noon);
            var presence = new NodeControllerPresence(clock);
            cockpit.WatchController(presence);
            var host = _Host(cockpit);
            await host.ApplySettingsAsync();

            presence.Seen("LAPTOP");
            window.UpdateLayout();
            Assert.Equal(AssistantActivity.Unavailable, host.Activity);

            // One missed poll is not a controller gone.
            clock.Advance(TimeSpan.FromSeconds(30));
            window.UpdateLayout();
            Assert.NotNull(_Notice(window));
            Assert.Equal(AssistantActivity.Unavailable, host.Activity);

            // The chosen term: a minute, spelled out here rather than read off the constant.
            clock.Advance(TimeSpan.FromSeconds(31));
            window.UpdateLayout();

            Assert.Null(cockpit.ActiveController);
            Assert.Null(_Notice(window));
            Assert.Equal(AssistantActivity.Ready, host.Activity);
            Assert.Null(host.UnavailableReason);
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 3: which machine, since when (local clock, short), and that it comes back by itself — and once the
    // conversation is back on screen, the same words in the composer, where the notice cannot cover the rows.
    [Fact]
    public void TheNoticeSaysWhichMachineSinceWhen_AndThatItComesBack() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var presence = new NodeControllerPresence(new FakeTimeProvider(Noon));
            cockpit.WatchController(presence);
            presence.Seen("LAPTOP");
            window.UpdateLayout();

            var expected = $"Controlled by LAPTOP since {Noon.ToLocalTime():HH:mm}. Your assistant here comes back by itself when that connection drops.";
            Assert.Equal(expected, _Notice(window)!.Text);
            Assert.Equal("Waits for the controller to let go, or start on a project below.", _Named<TextBox>(window, "InputBox").PlaceholderText);

            cockpit.SimpleStartScreenRequested = false;
            window.UpdateLayout();

            Assert.True(_Named<ItemsControl>(window, "TranscriptItems").IsEffectivelyVisible);
            Assert.Null(_Notice(window));
            Assert.Equal(expected, _Named<TextBox>(window, "InputBox").PlaceholderText);
        }
        finally
        {
            window.Close();
        }
    });

    // The takeover notice on screen, or null while there is none. The composer's placeholder is a TextBlock too,
    // and carries the same words once the conversation is back — not the notice.
    private static TextBlock? _Notice(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.IsEffectivelyVisible
                && t.Text?.StartsWith("Controlled by", StringComparison.Ordinal) == true
                && !t.GetVisualAncestors().Any(a => a is TextBox));

    private static T _Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    // The real host on the scene's cockpit, enabled and with a profile: what refuses the turn is the takeover and
    // nothing earlier in its start path.
    private static AssistantSessionHost _Host(CockpitViewModel cockpit)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = true });
        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantProfileSlot(new SessionProfile("assistant-local", new ClaudeConfig("/tmp/claude"))));
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>([]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>([]);

        return new AssistantSessionHost(
            cockpit, settings, profiles, sessionState,
            new SessionStateRecorder(sessionState, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance),
            catalog, Substitute.For<IAssistantMemory>(), NullLogger<AssistantSessionHost>.Instance);
    }

    // A settable clock whose timers fire when it is advanced past them — the fall-back is otherwise a minute's wait.
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> _alarms = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _alarms.Add((_now + dueTime, callback, state));
            return new _InertTimer();
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var alarm in _alarms.Where(alarm => alarm.Due <= _now).ToList())
            {
                _alarms.Remove(alarm);
                alarm.Callback(alarm.State);
            }
        }

        private sealed class _InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
