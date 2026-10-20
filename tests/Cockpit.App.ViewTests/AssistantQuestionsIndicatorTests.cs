using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;

namespace Cockpit.App.ViewTests;

// Assistant threads (§ 7.3): the chip counts the open questions while the assistant rests, in the waiting state rather
// than "needs you", and steps aside for anything the assistant is actively doing.
public sealed class AssistantQuestionsIndicatorTests
{
    [Fact]
    public void OpenQuestions_TakeTheRestingChip_AndTheNeedsYouState_ButNotAnActiveOne()
    {
        var indicator = new AssistantIndicatorViewModel { Activity = AssistantActivity.AwaitingOperator, OpenQuestionCount = 3 };

        Assert.True(indicator.ShowsQuestions);
        Assert.False(indicator.ShowsAwaitingOperator);
        Assert.Equal("3 questions for you", indicator.Label);
        Assert.Equal("Open to answer", indicator.Detail);

        indicator.OpenQuestionCount = 1;
        Assert.Equal("1 question for you", indicator.Label);

        indicator.Activity = AssistantActivity.Listening;
        Assert.False(indicator.ShowsQuestions);
        Assert.Equal("Listening", indicator.Label);

        indicator.Activity = AssistantActivity.AwaitingOperator;
        indicator.OpenQuestionCount = 0;
        Assert.True(indicator.ShowsAwaitingOperator);
    }
}
