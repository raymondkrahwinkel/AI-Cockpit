using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A reply taller than the transcript viewport — the shape AC-528's follow was never measured against, because
/// every case it was measured on fitted. ScrollIntoView brings a rect into view, and a rect taller than the
/// viewport is already in view once its top edge is, so the viewport stops moving at the head of the message
/// while the follow's own "are we at the bottom" test can never be satisfied again.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptTallReplyFollowTests
{
    [Fact]
    public async Task AReplyTallerThanTheViewport_IsStillFollowedToItsTail()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            var row = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "start of the reply.\n\n");
            vm.Transcript.Add(row);

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(200);

            for (var i = 0; i < 60; i++)
            {
                row.AppendText($"paragraph {i} of a long markdown answer that keeps growing and wrapping.\n\n");
                await Task.Delay(20);
            }

            // The follow itself runs off ScrollChanged via Dispatcher.UIThread.Post (SessionView's
            // _transcriptHandler), so it is queued rather than applied inline — pump the dispatcher to run it and
            // let the layout it drives settle, instead of hoping a fixed sleep outlasted the post.
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = view.TranscriptScroll!;
            var newest = view.TranscriptItems.ContainerFromIndex(view.TranscriptItems.ItemCount - 1);
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
        });
    }
}
