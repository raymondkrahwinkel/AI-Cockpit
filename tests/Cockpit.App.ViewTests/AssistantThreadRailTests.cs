using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Sessions;
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

        Assert.Equal(["Questions", "All", "General", "cockpit", "depot", "waymark", "Overview"],
            chat.ThreadItems.Select(item => item.ProjectId ?? item.Name));
        Assert.Equal(3, chat.OpenQuestionCount);
        Assert.Equal(2, chat.ThreadItems.First(item => item.ProjectId == "depot").WaitingCount);

        chat.SelectedThreadItem = chat.ThreadItems.First(item => item.ProjectId == "depot");
        Assert.All(chat.Session!.VisibleTranscript, row => Assert.True(row.IsInThread("depot")));
    }

    // § 9.6: a drop moves the prompt and what only followed it; the toast can turn it into an add, and Undo puts the
    // projects and their sources back exactly, so the reply follows its prompt again.
    [Fact]
    public void MovingAPrompt_TakesItsTurnAlong_AndUndoPutsBackItsSource()
    {
        var (chat, _) = _Chat();
        var session = chat.Session!;
        var prompt = session.Transcript.First(row => row.Text.StartsWith("Waarom synct", StringComparison.Ordinal));
        var reply = session.Transcript[session.Transcript.IndexOf(prompt) + 1];

        chat.DraggingRow = prompt;
        Assert.False(chat.CanDropOn(chat.ThreadItems.First(item => item.ProjectId == "waymark")));
        chat.Drop(chat.ThreadItems.First(item => item.ProjectId == "depot"), add: false);
        chat.DraggingRow = null;

        Assert.Equal(["depot"], reply.ThreadProjectIds);
        Assert.Equal(ProjectSource.Explicit, prompt.SourceFor("depot"));
        Assert.Equal("Moved to Depot, with 1 row of its turn", chat.MoveToastText);
        Assert.True(chat.CanKeepAlso);
        Assert.Equal("Keep in WayMark too", chat.KeepAlsoLabel);

        chat.KeepAlsoCommand.Execute(null);
        Assert.Equal(["depot", "waymark"], prompt.ThreadProjectIds.Order());

        chat.UndoMoveCommand.Execute(null);
        Assert.Equal(["waymark"], prompt.ThreadProjectIds);
        Assert.Equal(["waymark"], reply.ThreadProjectIds);
        Assert.Equal(ProjectSource.Inherited, reply.ThreadProjectSource);
        Assert.False(chat.HasMoveToast);
    }

    // § 9.6: a row that leaves the open thread hands the keyboard to the next row the thread still shows.
    [Fact]
    public void AfterAMoveOutOfTheThread_TheFocusGoesToTheNextRowItShows()
    {
        var (chat, _) = _Chat();
        chat.SelectedThreadItem = chat.ThreadItems.First(item => item.ProjectId == "depot");
        var session = chat.Session!;
        var inbox = session.VisibleTranscript.First(row => row.IsInboxRow);
        TranscriptEntryViewModel? focused = null;
        chat.RowFocusRequested += row => focused = row;

        // The inbox card takes the rows that followed it along, so the focus lands on the first row Depot still has.
        session.MoveToThread(inbox, "cockpit", add: false);

        Assert.Contains(focused!, session.VisibleTranscript);
        Assert.Same(session.Transcript.Skip(session.Transcript.IndexOf(inbox) + 1).First(session.VisibleTranscript.Contains), focused);
        chat.UndoMoveCommand.Execute(null);
        Assert.Same(inbox, focused);
    }

    [Fact]
    public void TheOverview_HasACardPerProject_WithWhatWaitsThere()
    {
        var (chat, _) = _Chat();

        chat.SelectedThreadItem = chat.ThreadItems.Single(item => item.Kind == AssistantThreadItemKind.Overview);

        Assert.True(chat.IsOverviewPage);
        Assert.False(chat.IsTranscriptPage);
        Assert.Equal([("cockpit", 1), ("depot", 2), ("waymark", 0)], chat.OverviewCards.Select(card => (card.ProjectId, card.Waiting)));
    }

    // The Simple stand's count opens Questions for you in the assistant's own column.
    [Fact]
    public void TheSimpleStandsCount_OpensQuestionsForYou()
    {
        var (chat, cockpit) = _Chat();

        cockpit.OpenAssistantQuestionsCommand.Execute(null);

        Assert.True(chat.IsQuestionsPage);
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
