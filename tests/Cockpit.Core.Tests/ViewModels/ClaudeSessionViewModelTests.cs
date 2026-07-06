using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="ClaudeSessionViewModel"/>'s transcript-shaping logic (the
/// "Thinking..." row lifecycle and the model launch argument) against a fake
/// <see cref="IClaudeSession"/>. <c>Apply</c> is invoked directly (it is <c>internal</c>,
/// visible here via <c>InternalsVisibleTo</c> on <c>Cockpit.App</c>) rather than through
/// <c>ConsumeEventsAsync</c>'s <c>Dispatcher.UIThread.InvokeAsync</c>, since no Avalonia
/// application/dispatcher is initialized in this test host.
/// </summary>
public class ClaudeSessionViewModelTests
{
    [Fact]
    public async Task StartAsync_UsesSelectedModel_WhenStartingSession()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var profile = new ClaudeProfile("default", @"C:\fake\.claude");
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        var vm = new ClaudeSessionViewModel(session, profileStore, loginChecker)
        {
            SelectedModel = new ModelOption("Haiku", "haiku"),
        };

        await vm.StartCommand.ExecuteAsync(null);

        await session.Received(1).StartAsync(profile, Arg.Any<string?>(), "haiku", Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void Apply_ThinkingDelta_AddsAThinkingEntry()
    {
        var vm = NewVm();

        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Transcript.Should().ContainSingle(t => t.Kind == TranscriptEntryKind.Thinking);
    }

    [Fact]
    public void Apply_TextDeltaAfterThinking_RemovesTheThinkingEntry()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "Here you go." });

        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.Thinking);
        vm.Transcript.Should().Contain(t => t.Kind == TranscriptEntryKind.AssistantText && t.Text == "Here you go.");
    }

    [Fact]
    public void Apply_ToolUseAfterThinking_RemovesTheThinkingEntry()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Read", InputJson = "{}" });

        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.Thinking);
        vm.Transcript.Should().Contain(t => t.Kind == TranscriptEntryKind.ToolUse);
    }

    [Fact]
    public void Apply_TurnCompletedAfterThinking_RemovesTheThinkingEntry()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.Thinking);
        vm.Transcript.Should().Contain(t => t.Kind == TranscriptEntryKind.TurnCompleted);
    }

    [Fact]
    public void Apply_TextDeltaWithNoPriorThinking_DoesNotThrow()
    {
        var vm = NewVm();

        var act = () => vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "hi" });

        act.Should().NotThrow();
    }

    [Fact]
    public void Apply_PermissionRequested_SetsStatusToNeedsAttention()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        vm.SessionStatus.Should().Be(SessionStatus.NeedsAttention);
    }

    [Fact]
    public void Apply_SessionStatusChangedWithNeedsAction_SetsStatusToNeedsAttention()
    {
        var vm = NewVm();

        vm.Apply(new SessionStatusChanged { SessionId = "S1", NeedsAction = "answer_question" });

        vm.SessionStatus.Should().Be(SessionStatus.NeedsAttention);
    }

    [Fact]
    public void Apply_SessionStatusChangedWithoutNeedsAction_LeavesStatusIdle()
    {
        var vm = NewVm();

        vm.Apply(new SessionStatusChanged { SessionId = "S1", StatusCategory = "review_ready" });

        vm.SessionStatus.Should().Be(SessionStatus.Idle);
    }

    [Fact]
    public void Apply_TurnCompleted_SetsStatusToDone()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        vm.SessionStatus.Should().Be(SessionStatus.Done);
    }

    [Fact]
    public async Task SendAsync_WhileTurnInFlight_SetsStatusToBusy()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        var vm = new ClaudeSessionViewModel(session, profileStore, loginChecker) { InputText = "hello" };

        await vm.SendCommand.ExecuteAsync(null);

        vm.SessionStatus.Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void Apply_TurnCompletedAfterPermissionRequest_PriorityGoesToNeedsAttention()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        vm.SessionStatus.Should().Be(SessionStatus.NeedsAttention);
    }

    [Fact]
    public void PermissionModes_WhenNotLocked_OfferOnlyTheThreeLiveModes()
    {
        var vm = NewVm();

        vm.PermissionModes.Select(mode => mode.Value).Should().Equal("default", "acceptEdits", "plan");
    }

    [Fact]
    public void PermissionModes_WhenLockedToBypass_CollapseToThatSingleLockedEntry()
    {
        var vm = NewVm();
        vm.SelectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode("bypassPermissions");

        vm.IsPermissionModeLocked = true;

        vm.PermissionModes.Should().ContainSingle()
            .Which.Value.Should().Be("bypassPermissions");
    }

    [Fact]
    public void Efforts_MapEachLevelToItsThinkingBudget()
    {
        var vm = NewVm();

        vm.Efforts.Select(e => (e.Value, e.MaxThinkingTokens)).Should().Equal(
            ("low", 4_000),
            ("medium", 12_000),
            ("high", 24_000),
            ("xhigh", 48_000),
            ("max", 64_000));
    }

    [Fact]
    public async Task StartAsync_AppliesTheSelectedEffortsBudgetOnceLive()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var profile = new ClaudeProfile("default", @"C:\fake\.claude");
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        var vm = new ClaudeSessionViewModel(session, profileStore, loginChecker)
        {
            SelectedEffort = new EffortOption("High", "high", 24_000),
        };

        await vm.StartCommand.ExecuteAsync(null);

        await session.Received(1).SetMaxThinkingTokensAsync(24_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SelectedEffortChanged_WhileLive_SendsTheNewBudget()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var profile = new ClaudeProfile("default", @"C:\fake\.claude");
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);
        var vm = new ClaudeSessionViewModel(session, profileStore, loginChecker);
        await vm.StartCommand.ExecuteAsync(null);
        session.ClearReceivedCalls();

        vm.SelectedEffort = new EffortOption("Max", "max", 64_000);

        await session.Received(1).SetMaxThinkingTokensAsync(64_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void SelectedEffortChanged_BeforeStart_DoesNotTouchTheSession()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        var vm = new ClaudeSessionViewModel(session, profileStore, loginChecker);

        vm.SelectedEffort = new EffortOption("High", "high", 24_000);

        session.DidNotReceive().SetMaxThinkingTokensAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowAlwaysExactTool_ResolvesTheSessionWithAnExactAlwaysRule()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(session, Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());
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
        entry.IsPendingPermission.Should().BeFalse();
        entry.PermissionDecision.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AllowAlwaysWildcardTool_ResolvesTheSessionWithAWildcardAlwaysRule()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(session, Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());
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

    private static ClaudeSessionViewModel NewVm()
    {
        var session = Substitute.For<IClaudeSession>();
        session.Events.Returns(EmptyEvents());
        var profileStore = Substitute.For<IClaudeProfileStore>();
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        return new ClaudeSessionViewModel(session, profileStore, loginChecker);
    }

    private static async IAsyncEnumerable<ClaudeSessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
