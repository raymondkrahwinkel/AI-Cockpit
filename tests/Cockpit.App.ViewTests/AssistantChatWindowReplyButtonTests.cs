using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Material.Icons;
using Material.Icons.Avalonia;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-935: the reply affordance on a transcript row, verified against the actual markup — the same reason
// AssistantChatWindowUserRowCopyButtonTests is (a wiring mistake passes a view-model-only test happily).
[Collection("avalonia")]
public sealed class AssistantChatWindowReplyButtonTests
{
    [Fact]
    public void ARow_ShowsAReplyButtonThatSetsItAsThePendingTarget() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");
        session.Transcript.Add(entry);

        var window = _Window(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var replyButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible
                                   && button.GetVisualDescendants().OfType<MaterialIcon>()
                                       .Any(icon => icon.Kind == MaterialIconKind.CommentArrowLeftOutline));

            Assert.Same(session.SetReplyTargetCommand, replyButton.Command);

            replyButton.Command!.Execute(replyButton.CommandParameter);

            Assert.Same(entry, session.PendingReplyTo);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ARowWithAReply_ShowsAMarkerThatJumpsToIt() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");
        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "looks fine to me") { ReplyTo = target };
        target.LatestReply = reply;
        session.Transcript.Add(target);
        session.Transcript.Add(reply);

        var window = _Window(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // The marker only shows on the answered row (target), never on a row with no reply.
            var markers = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible
                                  && button.GetVisualDescendants().OfType<MaterialIcon>()
                                      .Any(icon => icon.Kind == MaterialIconKind.CommentArrowRightOutline))
                .ToList();
            Assert.Single(markers);

            markers[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            // ScrollIntoView on a single-row scene is a no-op for scroll position, but stickToBottom coming off
            // and the jump-to-newest chevron reappearing is the observable side effect this click is meant to have.
            Assert.True(window.ScrollToBottomButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    private static AssistantChatWindow _Window(SessionViewModel session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        return new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(
                host,
                _FakeSettingsStore(),
                Substitute.For<IVoicePlaybackQueue>()),
        };
    }

    private static IAssistantSettingsStore _FakeSettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }
}
