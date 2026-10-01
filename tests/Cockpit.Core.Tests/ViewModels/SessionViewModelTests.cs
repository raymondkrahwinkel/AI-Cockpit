using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Tests.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Cockpit.App.Services;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="SessionViewModel"/>'s transcript-shaping logic (the "Thinking..." row
/// lifecycle) and the configured start path (<see cref="SessionViewModel.StartConfiguredAsync"/>,
/// which the New-session dialog drives) against a fake <see cref="ISessionDriver"/>. <c>Apply</c> is
/// invoked directly (it is <c>internal</c>, visible via <c>InternalsVisibleTo</c>) rather than through
/// <c>ConsumeEventsAsync</c>'s dispatcher, since no Avalonia dispatcher is initialized in this host.
/// </summary>
public class SessionViewModelTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task StartConfigured_InBypass_LocksThePanelPermissionMode()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.IsPermissionModeLocked);
        Assert.Equal("bypassPermissions", Assert.Single(vm.PermissionModes).Value);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_WhenTheLaunchFailsInBypass_DoesNotStrandThePanelOnAPhantomLock()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.StartAsync(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bad executable")));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(vm.IsPermissionModeLocked);
        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.PermissionModes.Select(mode => mode.Value));

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_InALiveMode_LeavesThePermissionModeUnlocked()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("plan"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(vm.IsPermissionModeLocked);
        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.PermissionModes.Select(mode => mode.Value));

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LocalToolSession_SeedsAutoApproveToolsFromTheProfileDefault()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"),
            Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.ShowToolAutoApprove);
        Assert.True(vm.AutoApproveTools);
        await session.Received(1).SetAutoApproveToolsAsync(true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LocalToolSession_WithoutTheProfileDefault_LeavesAutoApproveToolsOff()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.ShowToolAutoApprove);
        Assert.False(vm.AutoApproveTools);
        await session.DidNotReceive().SetAutoApproveToolsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void Apply_SubAgentToolCallNeedingPermission_IsFoundNestedByToolUseId()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "task-1", ToolName = "Task", InputJson = "{}" });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-1" });

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-1" });

        var anchor = Assert.Single(vm.Transcript);
        var nested = Assert.Single(anchor.SubAgentRows);
        Assert.True(nested.IsPendingPermission);
    }

    // AC-996: the needs-attention flag was set unconditionally while the consent card only exists where a row
    // does, so a permission whose tool-use event never arrived left the session waiting on the operator with
    // nothing on screen to answer. Neither observation in that ticket was this case, but nothing rules it out.
    [Fact]
    public void Apply_PermissionForAToolUseThatWasNeverSeen_StillGetsARowToApprove()
    {
        var vm = NewVm();

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "ghost", ToolName = "Bash", InputJson = """{"command":"ls"}""" });

        var row = Assert.Single(vm.Transcript);
        Assert.Equal(TranscriptEntryKind.ToolUse, row.Kind);
        Assert.Equal("ghost", row.ToolUseId);
        Assert.Equal("Bash", row.ToolName);
        Assert.True(row.IsPendingPermission);
        Assert.True(vm.HasPendingPermission);
        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
    }

    // And the same for a sub-agent's call whose lane this pane never resolved: top-level, because a row nested
    // under an anchor that may be collapsed is precisely the row the operator cannot reach.
    [Fact]
    public void Apply_PermissionForAnUnresolvedSubAgentCall_GetsATopLevelRow()
    {
        var vm = NewVm();

        vm.Apply(new PermissionRequested
        {
            SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-nobody-saw",
        });

        var row = Assert.Single(vm.Transcript);
        Assert.True(row.IsPendingPermission);
        Assert.Empty(row.SubAgentRows);
    }

    [Fact]
    public async Task Apply_PermissionRequested_ForAPreApprovedTool_AutoAllows_WithoutPromptingOrNeedsAttention()
    {
        // AC-215: a self-driving embedded run pre-authorizes its own control tools, so a permission request for one is
        // auto-allowed here instead of raising a prompt the autonomous run (composer off) has no one to answer — the
        // stall that left a run stuck on its own autopilot_step_done.
        const string preApproved = "mcp__cockpit-autopilot-run__autopilot_step_done";
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: [preApproved]);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = preApproved, InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = preApproved, InputJson = "{}" });

        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");
        Assert.False(entry.IsPendingPermission, "the pre-approved tool is auto-allowed, so no prompt is raised");
        Assert.Equal("Allowed", entry.PermissionDecision);
        Assert.NotEqual(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.Received(1).RespondToPermissionAsync("t1", true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Apply_PermissionRequested_ForAToolNotPreApproved_StillPrompts()
    {
        // The pre-approval is exact and narrow: a tool that is not on the list still raises the normal prompt, even in
        // a session that pre-approves others — file/shell/egress tools are never auto-allowed.
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: ["mcp__cockpit-autopilot-run__autopilot_step_done"]);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        Assert.True(vm.Transcript.Single(t => t.ToolUseId == "t1").IsPendingPermission);
        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.DidNotReceive().RespondToPermissionAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Apply_PermissionRequested_WhenPreApproveAllToolsIsSet_AutoAllowsEvenABashPrompt()
    {
        // "Worktree is the boundary" (Raymond 2026-07-23): an autonomous isolated run auto-allows every tool — not just
        // its own control tools — so its worker can run Bash/git/edits with no one to answer the prompt.
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: null, preApproveAllTools: true);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");
        Assert.False(entry.IsPendingPermission);
        Assert.Equal("Allowed", entry.PermissionDecision);
        Assert.NotEqual(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.Received(1).RespondToPermissionAsync("t1", true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AllowAlwaysExactTool_ResolvesTheSessionWithAnExactAlwaysRule()
    {
        var (vm, session) = await StartedVm();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Bash")
        {
            ToolUseId = "toolu_1",
            ToolName = "Bash",
            InputJson = """{"command":"ls"}""",
            IsPendingPermission = true,
        };

        await vm.AllowAlwaysExactToolCommand.ExecuteAsync(entry);

        await session.Received(1).AllowPermissionAlwaysAsync(
            "toolu_1", "Bash", """{"command":"ls"}""", PermissionRuleScope.Exact, Arg.Any<CancellationToken>());
        Assert.False(entry.IsPendingPermission);
        Assert.False(string.IsNullOrEmpty(entry.PermissionDecision));
    }

    [Fact]
    public async Task AllowAlwaysWildcardTool_ResolvesTheSessionWithAWildcardAlwaysRule()
    {
        var (vm, session) = await StartedVm();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Bash")
        {
            ToolUseId = "toolu_2",
            ToolName = "Bash",
            InputJson = """{"command":"ls"}""",
            IsPendingPermission = true,
        };

        await vm.AllowAlwaysWildcardToolCommand.ExecuteAsync(entry);

        await session.Received(1).AllowPermissionAlwaysAsync(
            "toolu_2", "Bash", """{"command":"ls"}""", PermissionRuleScope.Wildcard, Arg.Any<CancellationToken>());
    }

    private static SessionViewModel NewVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        return TestSessions.Pane(new SessionManager(FactoryFor(session)));
    }

    private sealed class ThrowingLogger : ILogger<SessionViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new IOException("disk full");
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Session)> StartedVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, session);
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Open until the runtime cancels it: a live driver's stream ends only when its process does (AC-693).
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    /// <summary>Wraps a fake driver in a factory so the view model resolves exactly that driver when it starts (the driver is now created from the factory once the profile is known).</summary>
    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
