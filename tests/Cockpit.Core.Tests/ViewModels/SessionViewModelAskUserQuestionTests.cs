using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

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
    public async Task PermissionRequested_ForAskUserQuestion_RendersTheQuestionsInsteadOfTheConsentButtons()
    {
        var (vm, _) = await _StartedAsync();

        var entry = _RaiseQuestion(vm, OneQuestion);

        Assert.True(entry.HasQuestionPrompts);
        Assert.Equal("Which suites?", entry.QuestionPrompts?[0].Question);
        Assert.Equal(["Core", "All"], entry.QuestionPrompts?[0].Options.Select(option => option.Label) ?? []);
        // The four consent buttons and the tool chip both stand down: none of them is an answer, and the chip's
        // raw JSON is the same questions spelled worse.
        Assert.False(entry.IsPendingToolPermission);
        Assert.False(entry.ShowToolBlock);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PermissionRequested_ForAnOrdinaryTool_KeepsTheConsentButtons()
    {
        var (vm, _) = await _StartedAsync();

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"ls"}""" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"ls"}""" });
        var entry = vm.Transcript.Single(row => row.ToolUseId == "t1");

        Assert.False(entry.HasQuestionPrompts);
        Assert.True(entry.IsPendingToolPermission);
        Assert.True(entry.ShowToolBlock);

        await vm.DisposeAsync();
    }

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
    public async Task SubmitQuestionAnswers_SendsTheChosenLabelsKeyedByQuestion_AndKeepsTheCardOnScreen()
    {
        var (vm, driver) = await _StartedAsync();
        var entry = _RaiseQuestion(vm, OneQuestion);
        entry.QuestionPrompts?[0].Options[1].SelectCommand.Execute(null);

        await vm.SubmitQuestionAnswersCommand.ExecuteAsync(entry);

        await driver.Received(1).RespondToPermissionAsync(
            "t1", true, Arg.Is<string>(json => _AnswerFor(json, "Which suites?") == "All"), Arg.Any<CancellationToken>());
        // The question and what was chosen stay put — a card that collapsed would take the agent's question with it.
        Assert.True(entry.HasQuestionPrompts);
        Assert.True(entry.QuestionPrompts?[0].IsAnswered);
        Assert.True(entry.QuestionPrompts?[0].Options[1].IsSelected);
        Assert.False(entry.IsPendingPermission);
        Assert.Equal("Answered", entry.PermissionDecision);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SubmitQuestionAnswers_StaysShutUntilEveryQuestionOnTheCardIsAnswered()
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

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SubmitQuestionAnswers_SendsTheOperatorsOwnWordingWhenTheyPickOther()
    {
        var (vm, driver) = await _StartedAsync();
        var entry = _RaiseQuestion(vm, OneQuestion);
        var prompt = entry.QuestionPrompts?[0];
        Assert.NotNull(prompt);
        prompt.SelectOtherCommand.Execute(null);
        prompt.OtherText = "Only the ones touching this diff";

        await vm.SubmitQuestionAnswersCommand.ExecuteAsync(entry);

        await driver.Received(1).RespondToPermissionAsync(
            "t1", true,
            Arg.Is<string>(json => _AnswerFor(json, "Which suites?") == "Only the ones touching this diff"),
            Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TheComposerBandSaysWhatItIsWaitingFor_AnAnswerRatherThanAPermission()
    {
        // AC-532's band says "waiting for permission", which on a question sends the operator looking for an Allow
        // button it deliberately does not have. Two sessions rather than two calls in one: the band tracks the
        // oldest outstanding call, so a second prompt stacked on the first would still be reporting the question.
        var (questionVm, _) = await _StartedAsync();
        _RaiseQuestion(questionVm, OneQuestion);

        var (toolVm, _) = await _StartedAsync();
        toolVm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t2", ToolName = "Bash", InputJson = "{}" });
        toolVm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t2", ToolName = "Bash", InputJson = "{}" });

        Assert.Equal("waiting for an answer", questionVm.ActiveToolActivityAgeText);
        Assert.Equal("waiting for permission", toolVm.ActiveToolActivityAgeText);

        await questionVm.DisposeAsync();
        await toolVm.DisposeAsync();
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
        var vm = new SessionViewModel(new SessionManager(factory));
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
