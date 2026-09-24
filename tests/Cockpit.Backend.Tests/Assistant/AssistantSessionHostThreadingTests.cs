using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Assistant;

// AC-1382: the host's state is reached from the MCP request thread, the runtime pump and the presence timer at once
// once it runs without the app. Each test parks one thread where the interleave matters and lets the other run.
public class AssistantSessionHostThreadingTests
{
    private readonly IAssistantSession _session = Substitute.For<IAssistantSession>();
    private readonly ISessionLauncher _launcher = Substitute.For<ISessionLauncher>();

    public AssistantSessionHostThreadingTests()
    {
        _session.IsSessionReady.Returns(true);
        _launcher.CreateAssistantSession().Returns(_session);
    }

    // Acceptance 1: A queues a clear and is parked reading IsBusy; B, the pump, sees the turn end and clears. Red
    // (a restart for each) when A's decision was taken before B's and both run it; green when B waits for A's section.
    [Fact]
    public async Task OneClearRequest_RacingTheTurnEnd_RestartsTheAssistantOnce()
    {
        var host = _Host();
        await host.EnsureStartedAsync();
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var parkNextRead = release;
        _session.IsBusy.Returns(_ =>
        {
            if (Interlocked.Exchange(ref parkNextRead, null) is { } gate)
            {
                parked.Set();
                gate.Wait();
            }

            return false;
        });

        var requester = Task.Run(() => host.RequestConversationClear());
        parked.Wait();
        var pump = Task.Run(() => _session.StateChanged += Raise.Event<EventHandler<bool>>(_session, false));
        pump.Wait(TimeSpan.FromMilliseconds(200));
        release.Set();
        await Task.WhenAll(requester, pump);

        _launcher.Received(2).CreateAssistantSession();
    }

    // Acceptance 2: a PropertyChanged handler that waits on another thread's call into the host. Red (the wait runs
    // out) when the host raises while it still holds its lock, which is how a UI hop in a handler would deadlock.
    [Fact]
    public async Task APropertyChangedHandler_WaitingOnAnotherThreadsCall_IsNotRaisedUnderTheLock()
    {
        var host = _Host();
        await host.EnsureStartedAsync();
        _session.IsBusy.Returns(true);
        bool? finished = null;
        host.PropertyChanged += (_, _) =>
            finished = Task.Run(() => host.RequestConversationClear()).Wait(TimeSpan.FromSeconds(2));

        _session.StateChanged += Raise.Event<EventHandler<bool>>(_session, false);

        Assert.True(finished);
    }

    private AssistantSessionHost _Host()
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

        return new AssistantSessionHost(
            _launcher, new NodeControllerPresence(), settings, profiles, sessionState,
            new SessionStateRecorder(sessionState, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance),
            catalog, Substitute.For<IAssistantMemory>(), NullLogger<AssistantSessionHost>.Instance);
    }
}
