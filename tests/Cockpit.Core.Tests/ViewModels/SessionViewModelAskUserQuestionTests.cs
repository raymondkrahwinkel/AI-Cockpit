using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;
using Cockpit.App.Composition;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-715: an <c>AskUserQuestion</c> arrives over the same permission callback as a tool approval, so the
/// session view model has to tell the two apart — a question renders as its own card and answers over the wire,
/// where Allow/Deny would have approved the question and left the agent waiting.
/// </summary>
public class SessionViewModelAskUserQuestionTests
{
    private const string ToolName = "AskUserQuestion";

    private const string OneQuestion = """
    {"questions":[{"question":"Which suites?","header":"Tests","multiSelect":false,
      "options":[{"label":"Core","description":"Fast"},{"label":"All","description":"Slow"}]}]}
    """;

    private const string TwoQuestions = """
    {"questions":[
      {"question":"Which suites?","multiSelect":false,"options":[{"label":"Core"}]},
      {"question":"Then what?","multiSelect":false,"options":[{"label":"Push"}]}
    ]}
    """;

    [Fact]
    public async Task PermissionRequested_ForAToolMerelyCarryingAQuestionsField_IsStillAnOrdinaryConsent()
    {
        // The tool name is the signal the SDK documents; a payload that happens to hold a `questions` array is not
        // a licence to swap a file-write's consent card for a set of answer buttons.
        var (vm, _) = await _StartedAsync();

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Write", InputJson = OneQuestion });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Write", InputJson = OneQuestion });

        Assert.True(vm.Transcript.Single(row => row.ToolUseId == "t1").IsPendingToolPermission);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SubmitQuestionAnswers_StaysShutUntilEveryQuestionIsAnswered_ThenSendsTheLabelsKeyedByQuestion_AndKeepsTheCard()
    {
        // One card carries all 1-4 questions of a call and sends once, so a half-filled card must not go: the SDK
        // reads the answers object by question text and would find a key missing.
        var (vm, driver) = await _StartedAsync();
        var entry = _RaiseQuestion(vm, TwoQuestions);

        Assert.False(entry.CanSubmitAnswers);
        entry.QuestionPrompts?[0].Options[0].SelectCommand.Execute(null);
        Assert.False(entry.CanSubmitAnswers);

        await vm.SubmitQuestionAnswersCommand.ExecuteAsync(entry);
        await driver.DidNotReceive().RespondToPermissionAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        entry.QuestionPrompts?[1].Options[0].SelectCommand.Execute(null);
        Assert.True(entry.CanSubmitAnswers);

        await vm.SubmitQuestionAnswersCommand.ExecuteAsync(entry);
        await driver.Received(1).RespondToPermissionAsync(
            "t1", true, Arg.Is<string>(json => _AnswerFor(json, "Then what?") == "Push"), Arg.Any<CancellationToken>());

        // The question and what was chosen stay put — a card that collapsed would take the agent's question with it.
        Assert.True(entry.HasQuestionPrompts);
        Assert.True(entry.QuestionPrompts?[1].IsAnswered);
        Assert.True(entry.QuestionPrompts?[1].Options[0].IsSelected);
        Assert.False(entry.IsPendingPermission);
        Assert.Equal("Answered", entry.PermissionDecision);

        // An Allow given from the strip goes to the session of its row only: both wait on "t2", so by id on the
        // wrong session would grant rights nobody gave.
        var (other, otherDriver) = await _StartedAsync();
        var otherRow = _RaisePermission(other, "t2");
        var ownRow = _RaisePermission(vm, "t2");
        await new CompanionAttentionItem("own/t1", CompanionAttentionKind.Permission, vm, ownRow, "Bash").RespondAsync(allow: true);

        await driver.Received(1).RespondToPermissionAsync("t2", true, null, Arg.Any<CancellationToken>());
        await otherDriver.DidNotReceive().RespondToPermissionAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.True(otherRow.IsPendingPermission);

        await other.DisposeAsync();
        await vm.DisposeAsync();
    }

    // ── AC-955: the assistant's own broker (ask_structured_question), which has no permission callback ────────

    private const string OneBrokerQuestion = """
    {"questions":[{"question":"Which profile?","multiSelect":false,
      "options":[{"label":"Programmer (Opus)"},{"label":"Programmer (Sonnet)"}]}]}
    """;

    private static TranscriptEntryViewModel _RaiseBrokerQuestion(SessionViewModel vm, string inputJson)
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Question, "Which profile?")
        {
            InputJson = inputJson,
            QuestionPrompts = AskUserQuestionViewModel.Parse(inputJson),
            IsPendingBrokerAnswer = true,
        };
        vm.Transcript.Add(entry);
        return entry;
    }

    /// <summary>
    /// Criterion 7: Send on a broker card takes the exact path a typed message takes — a new user-turn row with
    /// the wire-format answer, sent through the driver — rather than a permission response, because there is no
    /// permission callback for this call to ride.
    /// </summary>
    [Fact]
    public async Task SubmitQuestionAnswers_OnABrokerQuestion_SendsTheAnswerAsAnOrdinaryUserTurn()
    {
        var (vm, driver) = await _StartedAsync();
        var entry = _RaiseBrokerQuestion(vm, OneBrokerQuestion);
        entry.QuestionPrompts?[0].Options[1].SelectCommand.Execute(null);

        await vm.SubmitQuestionAnswersCommand.ExecuteAsync(entry);

        await driver.Received(1).SendUserMessageAsync(
            "\"Which profile?\" → Programmer (Sonnet)", Arg.Any<IReadOnlyList<Core.Sessions.ImageAttachment>?>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().RespondToPermissionAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.True(vm.Transcript.Last().Kind == TranscriptEntryKind.UserText);
        Assert.False(entry.IsPendingBrokerAnswer);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// Criterion 8: an unanswered broker card blocks nothing — the session can still start another turn.
    /// </summary>
    [Fact]
    public async Task AnUnansweredBrokerQuestion_DoesNotBlockTheSessionFromStartingAnotherTurn()
    {
        var (vm, driver) = await _StartedAsync();
        var entry = _RaiseBrokerQuestion(vm, OneBrokerQuestion);

        Assert.False(vm.IsBusy);
        vm.InputText = "ask something else in the meantime";
        await vm.SendCommand.ExecuteAsync(null);

        await driver.Received(1).SendUserMessageAsync(
            "ask something else in the meantime", Arg.Any<IReadOnlyList<Core.Sessions.ImageAttachment>?>(), Arg.Any<CancellationToken>());
        Assert.True(entry.IsPendingBrokerAnswer);
        Assert.True(entry.CanSubmitAnswers == false); // still no answer picked — remains open, not stuck

        await vm.DisposeAsync();
    }

    private static TranscriptEntryViewModel _RaisePermission(SessionViewModel vm, string id)
    {
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = id, ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = id, ToolName = "Bash", InputJson = "{}" });
        return vm.Transcript.Single(row => row.ToolUseId == id);
    }

    private static TranscriptEntryViewModel _RaiseQuestion(SessionViewModel vm, string inputJson)
    {
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = ToolName, InputJson = inputJson });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = ToolName, InputJson = inputJson });
        return vm.Transcript.Single(row => row.ToolUseId == "t1");
    }

    private static string? _AnswerFor(string answersJson, string question)
    {
        using var document = JsonDocument.Parse(answersJson);
        return document.RootElement.GetProperty(question).GetString();
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> _StartedAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        driver.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));

        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var vm = TestSessions.Pane(new SessionManager(factory));
        await vm.StartConfiguredAsync(
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
