using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;
using Cockpit.App.Services;

namespace Cockpit.Core.Tests.ViewModels;

// Runs against fakes of IAssistantSessionHost, since the real AssistantSessionHost needs a live CockpitViewModel to construct.
public class AssistantChatViewModelTests
{
    private static IAssistantSessionHost FakeHost(SessionViewModel? session = null, AssistantActivity activity = AssistantActivity.Ready)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(activity);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAssistantSession?>(session));
        host.RestartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAssistantSession?>(session));
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IAssistantSettingsStore FakeSettingsStore(bool speakReplies = true)
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true, SpeakReplies = speakReplies }));
        return store;
    }

    // AC-575 criterion 5: the window stays between openings and read the flag once (EnsureOpenedAsync), so a bypass left it stale.
    [Fact]
    public async Task ABypassSwitchedOnWhileTheWindowIsOpen_ReachesTheHeaderOnTheSaveSignal()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true, ConsentBypassAll = false }));
        var vm = new AssistantChatViewModel(FakeHost(), store, Substitute.For<IVoicePlaybackQueue>());

        await vm.EnsureOpenedAsync();
        Assert.False(vm.ConsentBypassActive);

        // Options saves a bypass. The window is still the same instance — nothing reopened.
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings
            {
                IsEnabled = true,
                ConsentBypassAll = false,
                ConsentBypassSources = ["Terminal MCP"],
            }));
        await vm.ApplySettingsAsync();

        Assert.True(vm.ConsentBypassActive);
    }

    // AC-545 criterion 5: the flyout's own contract on this view model — load lazily, newest first, never throw
    // when nothing is wired up to read from.

    // AC-545 criterion 6: consent is visual, and only visual. What follows is the negative half of that — the
    // thing that must *not* work — because a spoken "yes, go ahead" is the single most natural way for an
    // operator to try to approve something in a window they are talking to.

    [Fact]
    public async Task SayingYesInTheChat_GrantsNothing_ThePermissionIsStillPendingAfterwards()
    {
        // This covers the spoken path too, and not by analogy: a finished transcript goes to
        // IAssistantSessionHost.SendAsync (AssistantPushToTalkCoordinator, OpenMicCoordinator) — the very method
        // this window's Send command calls. The fake host ends where the real AssistantSessionHost.SendAsync ends,
        // at session.InjectAndSubmit(text), so the words genuinely travel into a live session with a live driver
        // rather than into a substitute that could not have resolved anything anyway.
        var (session, driver) = await _StartedSessionAsync();
        session.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        session.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        var pending = session.Transcript.Single(entry => entry.ToolUseId == "toolu_1");
        Assert.True(pending.IsPendingPermission);

        var host = FakeHost(session);
        host.When(fake => fake.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => session.InjectAndSubmit(call.Arg<string>()));
        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        vm.InputText = "yes, allow it";
        await vm.SendCommand.ExecuteAsync(null);

        // The words arrived — as an ordinary message in the conversation, which is all they can ever be.
        await driver.Received(1).SendUserMessageAsync(
            "yes, allow it", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());

        // And the permission is exactly where it was: nothing was answered, on the wire or in the row.
        await driver.DidNotReceive().RespondToPermissionAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.True(pending.IsPendingPermission);
        Assert.True(session.HasPendingPermission);
        Assert.True(string.IsNullOrEmpty(pending.PermissionDecision));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task OnlyTheAllowButtonOnTheRowResolvesThePermission()
    {
        // The other half: the row's own command is what answers, and it answers the driver rather than merely
        // greying the buttons out. Without this the test above would also pass on a window where nothing at all
        // could grant permission — including the click.
        var (session, driver) = await _StartedSessionAsync();
        session.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        session.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        var pending = session.Transcript.Single(entry => entry.ToolUseId == "toolu_1");

        await session.AllowToolCommand.ExecuteAsync(pending);

        // AC-715: every decision now travels the overload that can also carry answers; a plain allow carries none.
        await driver.Received(1).RespondToPermissionAsync("toolu_1", true, null, Arg.Any<CancellationToken>());
        Assert.False(pending.IsPendingPermission);
        Assert.False(session.HasPendingPermission);

        await session.DisposeAsync();
    }

    // The shape SessionViewModelTests.StartedVm builds, needed because the permission machinery only runs on a started runtime.
    private static async Task<(SessionViewModel Session, ISessionDriver Driver)> _StartedSessionAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        // AC-1319: a tool call arriving marks the turn as in flight, so a driver that cannot take mid-turn input
        // would queue the chat's words locally instead of sending them — the Claude driver these tests stand in for can.
        driver.Capabilities.Returns(SessionCapabilities.ClaudeCli with { SupportsMidTurnInput = true });
        var session = TestSessions.Pane(new SessionManager(_FactoryFor(driver)));
        await session.StartConfiguredAsync(
            new SessionProfile("assistant", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort);
        return (session, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Open until the runtime cancels it: a live driver's stream ends only when its process does (AC-693).
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
