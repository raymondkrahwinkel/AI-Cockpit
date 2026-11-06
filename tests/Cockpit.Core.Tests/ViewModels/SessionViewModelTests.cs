using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Tests.Shared;
using NSubstitute;
using Cockpit.App.Composition;

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

    private const string PreApproved = "mcp__cockpit-autopilot-run__autopilot_step_done";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartConfigured_InBypass_LocksThePanelPermissionMode_UnlessTheLaunchFails(bool launchFails)
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        if (launchFails)
        {
            session.StartAsync(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("bad executable")));
        }

        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // A failed launch must not strand the panel on a phantom lock.
        Assert.Equal(!launchFails, vm.IsPermissionModeLocked);
        Assert.Equal(
            launchFails ? new[] { "default", "acceptEdits", "plan" } : new[] { "bypassPermissions" },
            vm.PermissionModes.Select(mode => mode.Value));

        await vm.DisposeAsync();
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

    // AC-215: a self-driving run pre-authorizes its own control tools, which no one is there to answer. The list is
    // exact and narrow — Bash beside it still prompts — unless the run pre-approves everything ("worktree is the
    // boundary", Raymond 2026-07-23).
    [Theory]
    [InlineData(PreApproved, false, PreApproved, true)]
    [InlineData(PreApproved, false, "Bash", false)]
    [InlineData(null, true, "Bash", true)]
    public async Task Apply_PermissionRequested_IsAutoAllowedOnlyForAPreApprovedTool_OrWhenAllToolsAre(
        string? preApprovedTool, bool preApproveAll, string toolName, bool autoAllowed)
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = TestSessions.Pane(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: preApprovedTool is null ? null : [preApprovedTool], preApproveAllTools: preApproveAll);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = toolName, InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = toolName, InputJson = "{}" });

        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");
        Assert.Equal(!autoAllowed, entry.IsPendingPermission);
        Assert.Equal(!autoAllowed, vm.SessionStatus == SessionStatus.NeedsAttention);
        if (autoAllowed)
        {
            Assert.Equal("Allowed", entry.PermissionDecision);
            await session.Received(1).RespondToPermissionAsync("t1", true, Arg.Any<CancellationToken>());
        }
        else
        {
            await session.DidNotReceive().RespondToPermissionAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }

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

    // AC-1476: "Allow for this session" is drawn only where the server announced it, the key may answer and the line is up.
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public async Task AllowForSession_IsOnlyOfferedWhereTheServerKnowsIt(bool mayAnswer, bool serverKnows, bool linkUp, bool offered)
    {
        var vm = NewVm();
        await vm.FollowRemoteAsync(Substitute.For<ISessionHandle>(), "huis-cockpit");

        vm.SetRemoteLink(linkUp, mayAnswer, serverKnows);

        Assert.Equal(offered, vm.CanAllowForSession);
    }

    private static SessionViewModel NewVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        return TestSessions.Pane(new SessionManager(FactoryFor(session)));
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
