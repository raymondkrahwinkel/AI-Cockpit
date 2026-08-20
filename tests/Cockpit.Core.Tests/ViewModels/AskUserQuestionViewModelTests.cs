using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-715: the clarifying-question card's own logic — reading the SDK's <c>questions</c> payload, and the
/// selection rules that decide the single string each question sends back.
/// </summary>
public class AskUserQuestionViewModelTests
{
    private const string TwoQuestions = """
    {"questions":[
      {"question":"How should I format the output?","header":"Format","multiSelect":false,
       "options":[{"label":"Summary","description":"Brief overview"},{"label":"Detailed","description":"Full explanation"}]},
      {"question":"Which suites?","header":"Tests","multiSelect":true,
       "options":[{"label":"Core"},{"label":"View"}]}
    ]}
    """;

    [Fact]
    public void Parse_ReadsQuestionHeaderOptionsAndMultiSelect()
    {
        var prompts = AskUserQuestionViewModel.Parse(TwoQuestions);

        Assert.Equal(2, prompts.Count);
        Assert.Equal("How should I format the output?", prompts[0].Question);
        Assert.Equal("Format", prompts[0].Header);
        Assert.False(prompts[0].MultiSelect);
        Assert.Equal(["Summary", "Detailed"], prompts[0].Options.Select(option => option.Label));
        Assert.Equal("Brief overview", prompts[0].Options[0].Description);
        Assert.True(prompts[1].MultiSelect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"command":"ls"}""")]
    [InlineData("""{"questions":"not an array"}""")]
    [InlineData("""{"questions":[{"header":"No text"}]}""")]
    public void Parse_YieldsNothingForAnythingThatIsNotAQuestionPayload(string? inputJson)
    {
        // A row only renders as a question card when it genuinely carries one — and a question without text would
        // be a blank block with buttons under it.
        Assert.Empty(AskUserQuestionViewModel.Parse(inputJson));
    }

    [Fact]
    public void SelectOption_OnASingleSelectQuestion_ReplacesTheEarlierChoice()
    {
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[0];

        prompt.Options[0].SelectCommand.Execute(null);
        prompt.Options[1].SelectCommand.Execute(null);

        Assert.False(prompt.Options[0].IsSelected);
        Assert.Equal("Detailed", prompt.Answer);
    }

    [Fact]
    public void SelectOption_OnAMultiSelectQuestion_TogglesAndJoinsTheChosenLabels()
    {
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[1];

        prompt.Options[0].SelectCommand.Execute(null);
        prompt.Options[1].SelectCommand.Execute(null);
        Assert.Equal("Core, View", prompt.Answer);

        prompt.Options[0].SelectCommand.Execute(null);
        Assert.Equal("View", prompt.Answer);
    }

    [Fact]
    public void OnASingleSelectQuestion_OtherReplacesTheTickedOption_AndTickingAnOptionAgainReplacesOther()
    {
        // Single-select: the fallback and the offered options are mutually exclusive, the same rule the SDK's own
        // reference handler applies — a typed answer stands in for the pick rather than being added to it.
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[0];
        prompt.Options[0].SelectCommand.Execute(null);

        prompt.SelectOtherCommand.Execute(null);
        prompt.OtherText = "  Only the flaky ones  ";

        Assert.False(prompt.Options[0].IsSelected);
        Assert.Equal("Only the flaky ones", prompt.Answer);

        prompt.Options[1].SelectCommand.Execute(null);

        Assert.False(prompt.IsOtherSelected);
        Assert.Equal("Detailed", prompt.Answer);
    }

    /// <summary>
    /// AC-955, correction to the original grooming's criterion 5: on a multi-select question there is no reason
    /// for "Other" to exclude the ticked options — "Core, View and something of my own" is an ordinary answer, so
    /// the typed text joins the ticked labels as one more chosen value instead of replacing them.
    /// </summary>
    [Fact]
    public void OnAMultiSelectQuestion_OtherJoinsTheTickedOptions_RatherThanReplacingThem()
    {
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[1];
        prompt.Options[0].SelectCommand.Execute(null);

        prompt.SelectOtherCommand.Execute(null);
        prompt.OtherText = "  Only the flaky ones  ";

        Assert.True(prompt.Options[0].IsSelected);
        Assert.Equal("Core, Only the flaky ones", prompt.Answer);

        prompt.Options[1].SelectCommand.Execute(null);

        Assert.True(prompt.IsOtherSelected);
        Assert.Equal("Core, View, Only the flaky ones", prompt.Answer);

        // Toggling "Other" back off drops it from the answer without touching the ticked options.
        prompt.SelectOtherCommand.Execute(null);
        Assert.False(prompt.IsOtherSelected);
        Assert.Equal("Core, View", prompt.Answer);
    }

    [Theory]
    [InlineData("""{"questions":[{"question":"Q","options":[{"label":"A"},{"label":"B"}]}]}""", true)]
    [InlineData("""{"questions":[{"question":"Q","allowOther":true,"options":[{"label":"A"},{"label":"B"}]}]}""", true)]
    [InlineData("""{"questions":[{"question":"Q","allowOther":false,"options":[{"label":"A"},{"label":"B"}]}]}""", false)]
    public void Parse_ReadsAllowOther_DefaultingToTrueForANativeAskUserQuestionPayload(string inputJson, bool expected)
    {
        // AC-955: a native AskUserQuestion payload carries no `allowOther` field at all and its SDK guarantees the
        // fallback regardless, so an absent field must default to true — only ask_structured_question turns it off.
        Assert.Equal(expected, AskUserQuestionViewModel.Parse(inputJson)[0].AllowOther);
    }

    [Fact]
    public void HasAnswer_IsFalseUntilSomethingIsChosen_AndForAnEmptyOtherBox()
    {
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[0];
        Assert.False(prompt.HasAnswer);

        prompt.SelectOtherCommand.Execute(null);
        prompt.OtherText = "   ";

        Assert.False(prompt.HasAnswer);
    }

    [Fact]
    public void OnceAnswered_TheCardStopsAcceptingChoices()
    {
        var prompt = AskUserQuestionViewModel.Parse(TwoQuestions)[0];
        prompt.Options[0].SelectCommand.Execute(null);

        prompt.IsAnswered = true;
        prompt.Options[1].SelectCommand.Execute(null);

        Assert.All(prompt.Options, option => Assert.False(option.IsSelectable));
        Assert.Equal("Summary", prompt.Answer);
    }
}
