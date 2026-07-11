using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="ClaudeSessionViewModel"/>'s transcript-shaping logic (the "Thinking..." row
/// lifecycle) and the configured start path (<see cref="ClaudeSessionViewModel.StartConfiguredAsync"/>,
/// which the New-session dialog drives) against a fake <see cref="ISessionDriver"/>. <c>Apply</c> is
/// invoked directly (it is <c>internal</c>, visible via <c>InternalsVisibleTo</c>) rather than through
/// <c>ConsumeEventsAsync</c>'s dispatcher, since no Avalonia dispatcher is initialized in this host.
/// </summary>
public class ClaudeSessionViewModelTests
{
    private static readonly ClaudeProfile Profile = new("default", @"C:\fake\.claude");

    [Fact]
    public async Task StartConfigured_LaunchesWithTheChosenModel()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, new ModelOption("Haiku", "haiku"), SessionOptionCatalog.DefaultEffort);

        await session.Received(1).StartAsync(Profile, Arg.Any<string?>(), "haiku", Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_AppliesTheChosenEffortsBudgetOnceLive()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, new EffortOption("High", "high", 24_000));

        await session.Received(1).SetMaxThinkingTokensAsync(24_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_InBypass_LocksThePanelPermissionMode()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.IsPermissionModeLocked.Should().BeTrue();
        vm.PermissionModes.Should().ContainSingle().Which.Value.Should().Be("bypassPermissions");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_WhenTheLaunchFailsInBypass_DoesNotStrandThePanelOnAPhantomLock()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.StartAsync(Arg.Any<ClaudeProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bad executable")));
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.IsPermissionModeLocked.Should().BeFalse();
        vm.PermissionModes.Select(mode => mode.Value).Should().Equal("default", "acceptEdits", "plan");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_InALiveMode_LeavesThePermissionModeUnlocked()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("plan"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.IsPermissionModeLocked.Should().BeFalse();
        vm.PermissionModes.Select(mode => mode.Value).Should().Equal("default", "acceptEdits", "plan");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LocalToolSession_SeedsAutoApproveToolsFromTheProfileDefault()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var localProfile = new ClaudeProfile("ollama", ConfigDir: "",
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"),
            Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true));
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.ShowToolAutoApprove.Should().BeTrue();
        vm.AutoApproveTools.Should().BeTrue();
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
        var localProfile = new ClaudeProfile("ollama", ConfigDir: "",
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.ShowToolAutoApprove.Should().BeTrue();
        vm.AutoApproveTools.Should().BeFalse();
        await session.DidNotReceive().SetAutoApproveToolsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    /// <summary>
    /// <see cref="SessionPanelViewModel.ProviderBadge"/> lives on the shared base (#26) so the sidebar tile
    /// can bind to it regardless of session subtype; this proves a local provider's session sets it there.
    /// </summary>
    [Fact]
    public async Task StartConfigured_LocalSession_SetsTheBaseProviderBadge()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var localProfile = new ClaudeProfile("ollama", ConfigDir: "",
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.ProviderBadge.Should().Be("Ollama");

        await vm.DisposeAsync();
    }

    /// <summary>A Claude-CLI session needs no badge — it is the default provider and gets no sidebar/header pill.</summary>
    [Fact]
    public async Task StartConfigured_ClaudeCliSession_LeavesTheBaseProviderBadgeEmpty()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.ProviderBadge.Should().BeEmpty();

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
    public void Apply_FirstAssistantOutput_ClearsTheThinkingIndicator()
    {
        var vm = NewVm();
        vm.IsAwaitingResponse = true; // as a dispatched turn leaves it until the first sign of activity

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "hi" });

        vm.IsAwaitingResponse.Should().BeFalse();
    }

    [Fact]
    public void Apply_NonOutputEvent_LeavesTheThinkingIndicatorUp()
    {
        var vm = NewVm();
        vm.IsAwaitingResponse = true;

        // A connect/status event is not the assistant answering, so the model is still "thinking".
        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "", Tools = [] });

        vm.IsAwaitingResponse.Should().BeTrue();
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

        // A failed turn is surfaced as a row; a successful one is not (T4), so use an error here.
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });

        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.Thinking);
        vm.Transcript.Should().Contain(t => t.Kind == TranscriptEntryKind.TurnCompleted);
    }

    [Fact]
    public void Apply_SuccessfulTurnCompleted_AddsNoTurnRow()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.TurnCompleted);
        vm.SessionStatus.Should().Be(SessionStatus.Done);
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
    public void Apply_ToolResult_CouplesToItsToolUseRowByToolUseId()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Edit", InputJson = "{}" });

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_1", Content = "done", IsError = false });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        toolUse.HasResult.Should().BeTrue();
        toolUse.ResultText.Should().Be("done");
        vm.Transcript.Should().NotContain(t => t.Kind == TranscriptEntryKind.ToolResult);
    }

    [Fact]
    public void Apply_ToolResultWithNoMatchingToolUse_FallsBackToAStandaloneRow()
    {
        var vm = NewVm();

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_orphan", Content = "stray", IsError = false });

        vm.Transcript.Should().ContainSingle(t => t.Kind == TranscriptEntryKind.ToolResult);
    }

    [Fact]
    public void Apply_ToolResultError_MarksTheCoupledResultAsAnError()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_2", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_2", Content = "boom", IsError = true });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        toolUse.IsResultError.Should().BeTrue();
        toolUse.HasResult.Should().BeTrue();
    }

    [Fact]
    public void ToolHeader_CompactsToToolNameAndAShortHint()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested
        {
            SessionId = "S1", ToolUseId = "toolu_3", ToolName = "Bash", InputJson = """{"command":"dotnet build"}""",
        });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        toolUse.ToolHeader.Should().Contain("Bash").And.Contain("dotnet build");
    }

    [Fact]
    public void ResultIsCodeLike_ForJsonResult_IsTrueAndPrettyPrinted()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: X");
        entry.SetResult("""{"a":1,"b":[2,3]}""", isError: false);

        entry.ResultIsCodeLike.Should().BeTrue();
        entry.ResultDisplayText.Should().Contain("\n");
    }

    [Fact]
    public void ResultIsCodeLike_ForShortPlainResult_IsFalse()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: X");
        entry.SetResult("done", isError: false);

        entry.ResultIsCodeLike.Should().BeFalse();
        entry.ResultDisplayText.Should().Be("done");
    }

    [Fact]
    public void AssistantTextRow_RendersAsMarkdown()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Some **bold** prose.");

        entry.IsAssistantMarkdown.Should().BeTrue();
        entry.IsPlainNonMarkdown.Should().BeFalse();
    }

    [Fact]
    public void UserRow_RendersAsItsOwnBubbleNotMarkdownNorPlain()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "build the project");

        entry.IsUserRow.Should().BeTrue();
        entry.IsAssistantMarkdown.Should().BeFalse();
        entry.IsPlainNonMarkdown.Should().BeFalse();
    }

    [Fact]
    public async Task SendingAMessage_EchoesItAsAUserRowWithoutAPrefix()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "hello there";

        await vm.SendCommand.ExecuteAsync(null);

        var echo = vm.Transcript.Should().ContainSingle(t => t.Kind == TranscriptEntryKind.UserText).Subject;
        echo.Text.Should().Be("hello there");
        echo.IsUserRow.Should().BeTrue();
        await vm.DisposeAsync();
    }

    [Fact]
    public void ErrorRow_StaysOnThePlainPath()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Send failed: boom");

        entry.IsAssistantMarkdown.Should().BeFalse();
        entry.IsPlainNonMarkdown.Should().BeTrue();
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
        var (vm, _) = await StartedVm();
        vm.InputText = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        vm.SessionStatus.Should().Be(SessionStatus.Busy);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_BeforeStart_ShowsAFriendlyErrorAndKeepsTheText()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session)) { InputText = "hello" };

        await vm.SendCommand.ExecuteAsync(null);

        vm.InputText.Should().Be("hello");
        vm.Transcript.Should().ContainSingle(t => t.Kind == TranscriptEntryKind.Error)
            .Which.Text.Should().Contain("not started");
        await session.DidNotReceive().SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhileBusy_QueuesTheMessageInsteadOfSending()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";

        await vm.SendCommand.ExecuteAsync(null); // first send goes out immediately, turn now in flight
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null); // second lands in the queue while busy

        vm.QueuedMessages.Select(m => m.Text).Should().Equal("second");
        vm.InputText.Should().BeEmpty();
        await session.Received(1).SendUserMessageAsync("first", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await session.DidNotReceive().SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_DispatchesTheNextQueuedMessage()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        vm.QueuedMessages.Should().BeEmpty();
        await session.Received(1).SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        vm.SessionStatus.Should().Be(SessionStatus.Busy);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task RecallLastQueuedMessage_PullsTheNewestQueuedMessageBackIntoTheInput()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "queued one";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "queued two";
        await vm.SendCommand.ExecuteAsync(null);

        var recalled = vm.RecallLastQueuedMessage();

        recalled.Should().BeTrue();
        vm.InputText.Should().Be("queued two");
        vm.QueuedMessages.Select(m => m.Text).Should().Equal("queued one");
        await vm.DisposeAsync();
    }

    [Fact]
    public void RecallLastQueuedMessage_WithAnEmptyQueue_ReturnsFalseAndLeavesInputUntouched()
    {
        var vm = NewVm();

        vm.RecallLastQueuedMessage().Should().BeFalse();
        vm.InputText.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAQueuedChip_CancelsThatMessage()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "cancel me";
        await vm.SendCommand.ExecuteAsync(null);

        vm.QueuedMessages.Single().RemoveCommand.Execute(null);

        vm.QueuedMessages.Should().BeEmpty();
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });
        await session.DidNotReceive().SendUserMessageAsync("cancel me", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public void CanSend_IsFalseWithoutContentAndTrueOnceTextIsTyped()
    {
        var vm = NewVm();

        vm.CanSend.Should().BeFalse();

        vm.InputText = "hi";

        vm.CanSend.Should().BeTrue();
    }

    [Fact]
    public void TimestampText_IsTheArrivalTimeAsHoursAndMinutes()
    {
        var entry = new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "hi", new DateTimeOffset(2026, 7, 6, 14, 7, 0, TimeSpan.Zero));

        entry.TimestampText.Should().Be("14:07");
    }

    [Fact]
    public async Task ExitMessage_WithAutoCloseOn_IsStillSentAndClosesTheSessionWhenTheTurnCompletes()
    {
        var (vm, session) = await StartedVm();
        vm.AutoCloseOnExit = true;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "exit";
        await vm.SendCommand.ExecuteAsync(null);

        await session.Received(1).SendUserMessageAsync("exit", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        closeRequested.Should().BeFalse(); // not until the turn finishes

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "bye", IsError = false });

        closeRequested.Should().BeTrue();
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ExitMessage_WithAutoCloseOff_DoesNotCloseTheSession()
    {
        var (vm, _) = await StartedVm();
        vm.AutoCloseOnExit = false;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "exit";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        closeRequested.Should().BeFalse();
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task NonExitMessage_WithAutoCloseOn_DoesNotCloseTheSession()
    {
        var (vm, _) = await StartedVm();
        vm.AutoCloseOnExit = true;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "hello";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        closeRequested.Should().BeFalse();
        await vm.DisposeAsync();
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
    public void PermissionModes_WhenNotLocked_OfferOnlyTheThreeLiveModes()
    {
        var vm = NewVm();

        vm.PermissionModes.Select(mode => mode.Value).Should().Equal("default", "acceptEdits", "plan");
    }

    [Fact]
    public async Task SelectedEffortChanged_WhileLive_SendsTheNewBudget()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        session.ClearReceivedCalls();

        vm.SelectedEffort = new EffortOption("Max", "max", 64_000);

        await session.Received(1).SetMaxThinkingTokensAsync(64_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void SelectedEffortChanged_BeforeStart_DoesNotTouchTheSession()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));

        vm.SelectedEffort = new EffortOption("High", "high", 24_000);

        session.DidNotReceive().SetMaxThinkingTokensAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
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
        entry.IsPendingPermission.Should().BeFalse();
        entry.PermissionDecision.Should().NotBeNullOrEmpty();
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

    private static ClaudeSessionViewModel NewVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        return new ClaudeSessionViewModel(FactoryFor(session));
    }

    /// <summary>A started session (its event loop is live), so send-path tests exercise sending after start rather than the not-started guard (#16).</summary>
    [Fact]
    public async Task SdkSession_WhenAutoSubmitOn_SendsTheTranscriptRightAfterInjection()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var voice = Substitute.For<IVoicePushToTalkService>();
        voice.BeginHold().Returns(true);
        voice.EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>()).Returns("open the file");
        var voiceSettings = Substitute.For<IVoiceSettingsStore>();
        voiceSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9", AutoSubmitAfterVoice = true });

        var vm = new ClaudeSessionViewModel(FactoryFor(session), voice, voiceSettings);
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        for (var i = 0; i < 50 && !vm.AutoSubmitAfterVoice; i++)
        {
            await Task.Delay(10);
        }

        vm.BeginVoiceHold().Should().BeTrue();
        await vm.EndVoiceHoldAsync(applyCleanup: true);

        // Auto-submit sent the appended transcript rather than leaving it in the input box for review.
        await session.Received(1).SendUserMessageAsync("open the file", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        vm.InputText.Should().BeEmpty();

        await vm.DisposeAsync();
    }

    private static async Task<(ClaudeSessionViewModel Vm, ISessionDriver Session)> StartedVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new ClaudeSessionViewModel(FactoryFor(session));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, session);
    }

    private static async IAsyncEnumerable<ClaudeSessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>Wraps a fake driver in a factory so the view model resolves exactly that driver when it starts (the driver is now created from the factory once the profile is known).</summary>
    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<ClaudeProfile?>()).Returns(driver);
        return factory;
    }
}
