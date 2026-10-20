using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// Assistant threads (voorstel § 7.2, § 8): the rail stands on the chat's own width, the operator's choice wins where
// labels fit, the choice is kept per host, and below the label threshold the toggle opens an overlay instead.
[Collection("avalonia")]
public sealed class AssistantThreadRailTests
{
    private static (AssistantChatViewModel Chat, CockpitViewModel Cockpit) _Chat()
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(Screenshotter._AssistantThreadsConversation()));

        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        return (new AssistantChatViewModel(host, settings, Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit), cockpit);
    }

    [Theory]
    [InlineData(960, AssistantRailStand.Labels)]
    [InlineData(558, AssistantRailStand.Labels)]
    [InlineData(520, AssistantRailStand.Icons)]
    [InlineData(340, AssistantRailStand.Icons)]
    [InlineData(238, AssistantRailStand.Picker)]
    public void TheRail_StandsOnTheChatsOwnWidth(double width, AssistantRailStand expected)
    {
        var (chat, _) = _Chat();

        chat.ViewWidth = width;

        Assert.Equal(expected, chat.RailStand);
    }

    [Fact]
    public async Task IconsOnly_IsKeptPerHost_AndWinsWhereLabelsFit()
    {
        var (chat, cockpit) = _Chat();
        chat.ViewWidth = 960;

        await chat.ToggleRailCommand.ExecuteAsync(null);

        Assert.Equal(AssistantRailStand.Icons, chat.RailStand);
        Assert.Equal(AssistantThreadRail.IconsOnly, cockpit.AssistantThreadRailWindow);
        Assert.Equal(AssistantThreadRail.Labels, cockpit.AssistantThreadRailDock);

        chat.IsDocked = true;
        Assert.Equal(AssistantRailStand.Labels, chat.RailStand);
    }

    [Fact]
    public async Task WhereLabelsDoNotFit_TheToggleOpensTheOverlay_AndPickingAThreadClosesIt()
    {
        var (chat, cockpit) = _Chat();
        chat.ViewWidth = 520;

        await chat.ToggleRailCommand.ExecuteAsync(null);

        Assert.True(chat.IsRailOverlayOpen);
        Assert.Equal(AssistantRailStand.Icons, chat.RailStand);
        Assert.Equal(AssistantThreadRail.Labels, cockpit.AssistantThreadRailWindow);

        chat.SelectedThreadItem = chat.ThreadItems.First(item => item.ProjectId == "depot");
        Assert.False(chat.IsRailOverlayOpen);
    }

    [Fact]
    public void TheRail_ListsGeneralFirst_ThenTheProjectsByName_WithTheirWaitingCounts()
    {
        var (chat, _) = _Chat();

        Assert.Equal(["Questions", "All", "General", "cockpit", "depot", "waymark"],
            chat.ThreadItems.Select(item => item.ProjectId ?? item.Name));
        Assert.Equal(3, chat.OpenQuestionCount);
        Assert.Equal(2, chat.ThreadItems.First(item => item.ProjectId == "depot").WaitingCount);

        chat.SelectedThreadItem = chat.ThreadItems.First(item => item.ProjectId == "depot");
        Assert.All(chat.Session!.VisibleTranscript, row => Assert.True(row.IsInThread("depot")));
    }

    [Fact]
    public void AnAnsweredQuestion_LeavesQuestionsForYou_ForAnsweredToday()
    {
        var (chat, _) = _Chat();
        var open = chat.Session!.OpenQuestions[^1];
        var answeredBefore = chat.AnsweredToday.Count;

        open.IsPendingBrokerAnswer = false;

        Assert.Equal(2, chat.OpenQuestionCount);
        Assert.Equal(answeredBefore + 1, chat.AnsweredToday.Count);
        Assert.Same(open, chat.AnsweredToday[0]);
    }
}
