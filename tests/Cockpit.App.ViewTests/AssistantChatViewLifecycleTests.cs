using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-952: the chat surface is a <see cref="AssistantChatView"/> now, and everything it wires hangs off attach
/// and detach rather than a window's Opened/Closed. The failure this guards is the quiet one — a view that is
/// re-hosted (AC-953's dock/undock) and never wires itself back up, so the transcript simply stops following
/// with nothing on screen to say so.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatViewLifecycleTests
{
    [Fact]
    public async Task MovingTheViewToAnotherHost_KeepsTheConversation_AndKeepsFollowingIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();
            for (var i = 0; i < 40; i++)
            {
                session.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText, $"row {i} of the conversation so far."));
            }

            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(session);
            var viewModel = new AssistantChatViewModel(
                host, Substitute.For<IAssistantSettingsStore>(), Substitute.For<IVoicePlaybackQueue>());

            var view = new AssistantChatView { DataContext = viewModel };
            var first = new Window { Width = 420, Height = 520, Content = view };
            var second = new Window { Width = 420, Height = 520 };

            first.Show();
            first.UpdateLayout();
            await Task.Delay(200);

            try
            {
                var followedInTheFirstHost = view.TranscriptScroll!.Offset.Y;

                // The host swap: out of one visual tree and into another, the same view instance throughout.
                first.Content = null;
                Dispatcher.UIThread.RunJobs();
                second.Content = view;
                second.Show();
                second.UpdateLayout();

                // The re-attach re-wires TranscriptScroll.ScrollChanged and the transcript's CollectionChanged
                // handler, but both follow the tail off Dispatcher.UIThread.Post (AssistantChatView._transcriptHandler)
                // rather than inline — pump the dispatcher to run the queued follow and let the layout it drives
                // settle, instead of hoping a fixed sleep outlasted the post (same pattern as
                // AssistantChatWindowTallReplyFollowTests).
                Dispatcher.UIThread.RunJobs();
                second.UpdateLayout();

                // Nothing about the conversation belongs to a host: leaving one must not dispose the view model
                // or drop the session (AssistantChatWindow.OnClosed still owns that, the view never does).
                Assert.Same(session, viewModel.Session);
                Assert.True(viewModel.HasMessages);

                // And the follow is wired again — this is what a missed re-attach loses, silently.
                var before = view.TranscriptScroll!.Offset.Y;
                session.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText, "a reply that arrives in the second host"));
                Dispatcher.UIThread.RunJobs();
                second.UpdateLayout();

                Assert.True(followedInTheFirstHost > 0, "the first host has to have been following, or this proves nothing");
                Assert.True(
                    view.TranscriptScroll!.Offset.Y > before,
                    "a row arriving after the host swap must still scroll into view");
            }
            finally
            {
                second.Close();
                first.Close();
            }
        });
    }
}
