using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1316: the Simple stand's column is the assistant's conversation from the first moment, with the start offer
/// (AC-1304) as that conversation's empty state — typing needs no project picked first. The gate that stood in
/// front of it was a circle: the column showed the chat view once the session existed, and the session started
/// once a host showed the chat view.
/// </summary>
public class Ac1316DirectTypingTests
{
    // The column holds the assistant before its session exists — that is the circle broken. With an agent
    // picked it still steps aside for that agent's pane.
    [Fact]
    public void TheColumn_HoldsTheAssistant_BeforeItsSessionExists()
    {
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        var agent = new SessionViewModel { Title = "agent" };
        cockpit.Sessions.Add(agent);
        _Chat(cockpit, session: null);

        Assert.Null(cockpit.AssistantRootSession);
        Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.True(cockpit.SimpleStandShowsTheStartScreen);

        cockpit.SimpleSelectedSession = agent;

        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.False(cockpit.SimpleStandShowsTheStartScreen);
    }

    // The offer is an offer: it stands while nothing has been said, goes when the conversation has content, comes
    // back on `+ New session`, and typing is as much an answer as picking a card. Only the Simple stand's host
    // draws it — the window and the dock keep their plain empty state.
    [Fact]
    public async Task TheOffer_StandsDownOnTheFirstMessage_AndComesBackOnNewSession()
    {
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        var session = new SessionViewModel { Title = "Assistant" };
        session.Transcript.Clear();
        var chat = _Chat(cockpit, session);

        Assert.False(chat.ShowsStartOffer);
        chat.IsSimpleViewHost = true;
        Assert.True(chat.ShowsStartOffer);

        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Hello."));
        Assert.False(chat.ShowsStartOffer);

        cockpit.ShowSimpleStartScreenCommand.Execute(null);
        Assert.True(chat.ShowsStartOffer);

        chat.InputText = "what is open on AC-1316?";
        await chat.SendCommand.ExecuteAsync(null);
        Assert.False(chat.ShowsStartOffer);
    }

    // The bug found on the way: SendAsync cleared the box before the host answered, so a message typed while the
    // assistant was switched off vanished without a word. Nothing was sent, so the words stay where they were typed.
    [Fact]
    public async Task ASendTheHostRefuses_LeavesTheWordsInTheBox()
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns((SessionViewModel?)null);
        host.Activity.Returns(AssistantActivity.Ready);
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                host.Activity.Returns(AssistantActivity.Unavailable);
                return Task.CompletedTask;
            });
        var chat = new AssistantChatViewModel(host, _Settings(), Substitute.For<IVoicePlaybackQueue>());

        chat.InputText = "is the invoice run done?";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal("is the invoice run done?", chat.InputText);
    }

    private static AssistantChatViewModel _Chat(CockpitViewModel cockpit, SessionViewModel? session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(AssistantActivity.Ready);
        return new AssistantChatViewModel(host, _Settings(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);
    }

    private static IAssistantSettingsStore _Settings()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }
}
