using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-953 route R2: docking and undocking build a fresh <see cref="AssistantChatView"/> per host on the same view
/// model, so nothing is reparented. What that route has to prove is the one piece of state a fresh view would
/// otherwise lose — where the transcript was scrolled to. The conversation itself needs no proving here: it lives
/// on the session, which neither host touches (AssistantChatViewLifecycleTests covers that).
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatDockHandoverTests
{
    private static AssistantChatViewModel _Chat(SessionViewModel session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        return new AssistantChatViewModel(
            host, Substitute.For<IAssistantSettingsStore>(), Substitute.For<IVoicePlaybackQueue>());
    }

    private static SessionViewModel _Conversation(int rows)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var i = 0; i < rows; i++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText, $"row {i} of the conversation so far."));
        }

        return session;
    }

    // The topmost row still on screen — what the operator is reading from, and the thing the handover has to
    // land back on. Measured the same way the view measures it, so the assertion is about the result and not
    // about the arithmetic.
    private static int _TopVisibleRow(AssistantChatView view)
    {
        var items = view.GetVisualDescendants().OfType<ItemsControl>()
            .First(control => control.Name == "TranscriptItems");

        for (var index = 0; index < items.ItemCount; index++)
        {
            if (items.ContainerFromIndex(index) is { } row
                && row.TranslatePoint(new Point(0, 0), view.TranscriptScroll) is { } top
                && top.Y + row.Bounds.Height > 0)
            {
                return index;
            }
        }

        return -1;
    }

    [Fact]
    public async Task ScrolledBackThroughTheConversation_TheNextHostOpensOnTheSameRow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = _Conversation(60);
            var chat = _Chat(session);

            // The floating window, standing in for either host: what matters is that this view leaves the visual
            // tree and a *different* view is built for the next one — the dock rail does exactly this.
            var floating = new Window { Width = 420, Height = 520, Content = new AssistantChatView { DataContext = chat } };
            floating.Show();
            floating.UpdateLayout();
            await Task.Delay(200);

            try
            {
                var before = (AssistantChatView)floating.Content!;

                // The operator wheels back through the history, which is what stops the follow — a view that is
                // still parked at the tail has nothing to hand over, and follows the tail in the next host anyway.
                for (var tick = 0; tick < 8; tick++)
                {
                    floating.MouseWheel(new Point(floating.Width / 2, floating.Height / 3), new Vector(0, 1));
                    floating.UpdateLayout();
                    await Task.Delay(30);
                }

                var readingFrom = _TopVisibleRow(before);
                Assert.True(readingFrom > 0, "the wheel must actually have moved off the tail for this to test anything");

                // The dock swap, in the order the coordinator does it: the old host goes first, so the leaving
                // view has written its position before the arriving one reads it.
                floating.Content = null;
                Dispatcher.UIThread.RunJobs();

                var after = new AssistantChatView { DataContext = chat };
                floating.Content = after;
                floating.UpdateLayout();
                await Task.Delay(200);

                Assert.Equal(readingFrom, _TopVisibleRow(after));
            }
            finally
            {
                floating.Close();
            }
        });
    }

    [Fact]
    public async Task ParkedAtTheNewestRow_TheNextHostIsParkedThereToo()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = _Conversation(60);
            var chat = _Chat(session);

            var floating = new Window { Width = 420, Height = 520, Content = new AssistantChatView { DataContext = chat } };
            floating.Show();
            floating.UpdateLayout();
            await Task.Delay(200);

            try
            {
                // Nothing to restore: following the tail is what a fresh view does on its own, so the handover
                // must stay out of the way rather than pin the new view to the row the old one happened to top out on.
                floating.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Null(chat.TranscriptAnchor);

                var after = new AssistantChatView { DataContext = chat };
                floating.Content = after;
                floating.UpdateLayout();
                await Task.Delay(200);

                var chevron = after.GetVisualDescendants().OfType<Button>()
                    .First(button => button.Name == "ScrollToBottomButton");
                Assert.False(chevron.IsVisible, "still following the tail, so there is nothing to jump to");
            }
            finally
            {
                floating.Close();
            }
        });
    }
}
