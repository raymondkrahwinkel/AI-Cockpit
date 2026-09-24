using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Assistant;

// AC-1379: the assistant's host and its chat-channel seam without the app — a fake runtime behind the backend launcher,
// a presence on a fake clock, and a channel that is the test itself.
public class AssistantSessionHostBackendTests
{
    private const string Allowed = "117";

    private readonly SessionRegistry _registry = new();
    private readonly ISessionRuntime _runtime = Substitute.For<ISessionRuntime>();
    private readonly ISessionManager _manager = Substitute.For<ISessionManager>();
    private readonly ManualClock _clock = new();

    public AssistantSessionHostBackendTests()
    {
        _runtime.IsRunning.Returns(true);
        _manager.Create(Arg.Any<SessionProfile?>()).Returns(_runtime);
    }

    // Acceptance 1: red when the assistant is not the registry's `Assistant` under its pane id, or cannot take a prompt.
    [Fact]
    public async Task TheAssistant_StartsWithoutTheApp_AsTheRegistrysAssistant_AndTakesAPrompt()
    {
        await _Host(new NodeControllerPresence(_clock)).SendAsync("hello");

        var assistant = Assert.IsAssignableFrom<ISessionHandle>(_registry.Assistant);
        Assert.Equal((AssistantIdentity.PaneId, true), (assistant.PaneId, assistant.CanTakeAPrompt));
        Assert.Empty(_registry.All);
        await _runtime.Received(1).SendUserMessageAsync("hello", Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());
    }

    // Acceptance 2 (AC-1321): a controller seen within the window holds the turn; once the window has run out on the
    // clock it is gone and the turn goes. Red when the hold does not follow the presence.
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(61, true, 1)]
    public async Task AControllerSeen_HoldsTheTurn_UntilItsWindowRunsOut(int secondsLater, bool turnGoes, int sends)
    {
        var presence = new NodeControllerPresence(_clock);
        var host = _Host(presence);
        presence.Seen("laptop");
        _clock.Advance(TimeSpan.FromSeconds(secondsLater));

        await host.SendAsync("hello");

        Assert.Equal((turnGoes, !turnGoes), (host.Session is not null, host.UnavailableReason?.StartsWith("Controlled by laptop") == true));
        await _runtime.Received(sends).SendUserMessageAsync("hello", Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());
    }

    // Acceptance 3: what the channel sends reaches the assistant, and the answer comes back to the channel as rows.
    [Fact]
    public async Task AChannelMessage_ReachesTheAssistant_AndItsAnswerComesBackToTheChannel()
    {
        var access = Assert.IsType<AssistantChannelAccess>(AssistantChannelAccess.ForSingleUser(Allowed).Access);
        using var gateway = new AssistantChannelGateway(
            new AssistantChannelContribution { Id = "channel-1", Name = "Test channel", Access = access },
            _Host(new NodeControllerPresence(_clock)),
            Substitute.For<IConsentBroker>(),
            NullLogger<AssistantChannelGateway>.Instance);
        var rows = new List<AssistantChannelRow>();
        gateway.RowChanged += (_, row) => rows.Add(row);

        var sent = await gateway.SendAsync(Allowed, "is the build green?");
        _runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new AssistantTextCompleted { SessionId = "S1", Text = "It is." });

        Assert.Equal(AssistantChannelSendResult.Sent(), sent);
        Assert.Equal(
            [(AssistantChannelRowKind.UserText, "is the build green?"), (AssistantChannelRowKind.AssistantText, "It is.")],
            rows.Where(row => !row.IsUpdate).Select(row => (row.Kind, row.Text)));
    }

    private AssistantSessionHost _Host(INodeControllerPresence presence)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = true });
        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantProfileSlot(new SessionProfile("assistant", new ClaudeConfig("/fake/.claude"))));
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>([]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>([]);
        var launcher = new SessionLauncher(
            new WorkspaceSettings(), Substitute.For<IWorkspaceSettingsStore>(), Substitute.For<IProjectStore>(), _registry, _manager, _clock);

        return new AssistantSessionHost(
            launcher, presence, settings, profiles, sessionState,
            new SessionStateRecorder(sessionState, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance),
            catalog, Substitute.For<IAssistantMemory>(), NullLogger<AssistantSessionHost>.Instance);
    }

    // A clock that only moves when told to, firing whatever timer came due on the way.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> _alarms = [];
        private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _alarms.Add((_now + dueTime, callback, state));
            return Substitute.For<ITimer>();
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
    }
}
