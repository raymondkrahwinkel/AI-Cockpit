using Avalonia;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-777: AC-774 virtualised this window's transcript but left the follow logic calling
/// `TranscriptScroll.ScrollToEnd()`, i.e. jumping to the panel's estimated `Extent - Viewport` — an estimate
/// the panel then corrects on its next arrange, which is the "scroll position jumps back into the history"
/// regression reported. Same case as TranscriptTallReplyFollowTests, aimed at this window's own transcript.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatWindowTallReplyFollowTests
{
    [Fact]
    public async Task AReplyTallerThanTheViewport_IsStillFollowedToItsTail()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();
            var row = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "start of the reply.\n\n");
            session.Transcript.Add(row);

            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(session);

            var window = new AssistantChatWindow
            {
                Width = 420,
                Height = 560,
                DataContext = new AssistantChatViewModel(
                    host,
                    Substitute.For<IAssistantSettingsStore>(),
                    Substitute.For<IVoicePlaybackQueue>()),
            };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(200);

            try
            {
                for (var i = 0; i < 60; i++)
                {
                    row.AppendText($"paragraph {i} of a long markdown answer that keeps growing and wrapping.\n\n");
                    await Task.Delay(20);
                }

                await Task.Delay(400);

                var scroll = window.TranscriptScroll;
                var newest = window.TranscriptItems.ContainerFromIndex(window.TranscriptItems.ItemCount - 1);
                Assert.NotNull(newest);

                // Guards the premise as much as the fix: a row that fits the viewport proves nothing here.
                Assert.True(
                    newest!.Bounds.Height > scroll.Viewport.Height,
                    $"the reply is {newest.Bounds.Height:F0}px in a {scroll.Viewport.Height:F0}px viewport — not the tall case");

                var bottom = newest.TranslatePoint(new Point(0, newest.Bounds.Height), scroll);
                Assert.NotNull(bottom);
                Assert.True(
                    bottom!.Value.Y <= scroll.Viewport.Height + 1.0,
                    $"the tail of the reply sits {bottom.Value.Y - scroll.Viewport.Height:F0}px below the viewport: the follow gave up");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
