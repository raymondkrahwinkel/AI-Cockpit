using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1265: a fenced code block carries no blank line for AC-1238's split to find, so it used to grow as one
/// row far past the viewport — the shape that makes the virtualising panel re-anchor its average on that one
/// row, which is the scrollbar thumb jumping between lengths frame on frame. It is split on a line boundary
/// now, and these are the two things that must not have been bought with it: the block still reads as one box,
/// and Copy still hands over the whole block rather than the fragment the button sits under.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptSplitCodeBlockTests
{
    // Long enough to cross the row bound several times at any plausible setting of it.
    private const int CodeLines = 90;

    private static void _StreamACodeBlock(SessionViewModel vm)
    {
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here it is:\n\n```csharp\n" });
        for (var i = 0; i < CodeLines; i++)
        {
            vm.Apply(new AssistantTextDelta
            {
                SessionId = "S1",
                BlockIndex = 0,
                Text = $"    var line{i} = ComputeSomethingWithARatherLongName(argument{i});\n",
            });
        }

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "```\n\n" });
    }

    private static List<Border> _CodeFrames(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.GetType().Name == "CodeBlockBorder")
            .ToList();

    private static async Task _SettleAsync(Window window, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }

    [Fact]
    public async Task ACodeBlockSplitAcrossRows_StillDrawsAsOneBox()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            _StreamACodeBlock(vm);

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 1400 };
            window.Show();
            await _SettleAsync(window, TimeSpan.FromMilliseconds(900));

            // The premise: without a split there is one row and nothing below proves anything.
            var spanRows = vm.Transcript.Where(row => row.StartsInsideCodeBlock || row.EndsInsideCodeBlock).ToList();
            Assert.True(
                spanRows.Count >= 2,
                $"the {CodeLines}-line code block landed in {spanRows.Count} row(s): it was never split, so this "
                + "says nothing about how a split one draws");

            var frames = _CodeFrames(view.TranscriptItems);
            Assert.True(
                frames.Count >= 2,
                $"only {frames.Count} code box(es) were drawn for {spanRows.Count} rows of one block — the walk "
                + "found nothing to judge");

            // Every edge where one fragment meets the next: no rounding, no border, and no gap. Any one of the
            // three left in is what made a split block read as a row of separate boxes.
            var scroll = view.TranscriptScroll!;
            for (var i = 0; i < frames.Count - 1; i++)
            {
                var above = frames[i];
                var below = frames[i + 1];
                var bottom = above.TranslatePoint(default, scroll)!.Value.Y + above.Bounds.Height;
                var top = below.TranslatePoint(default, scroll)!.Value.Y;

                Assert.True(
                    Math.Abs(top - bottom) <= 1.0,
                    $"box {i} ends at {bottom:F0} and box {i + 1} starts at {top:F0}: {top - bottom:F0}px of daylight "
                    + "between two halves of one code block");
                Assert.True(
                    above.CornerRadius.BottomLeft == 0 && above.CornerRadius.BottomRight == 0,
                    $"box {i} keeps its bottom corners rounded ({above.CornerRadius}) although the block carries on below");
                Assert.True(
                    below.CornerRadius.TopLeft == 0 && below.CornerRadius.TopRight == 0,
                    $"box {i + 1} keeps its top corners rounded ({below.CornerRadius}) although it carries a block on");
                Assert.True(
                    above.BorderThickness.Bottom == 0 && below.BorderThickness.Top == 0,
                    $"a hairline is drawn where box {i} meets box {i + 1} "
                    + $"(bottom {above.BorderThickness.Bottom}, top {below.BorderThickness.Top})");
            }

            // The label belongs to the fragment that opened the fence and to no other, or one block reads as
            // several no matter how well the boxes meet. Counted from the top of the transcript: the follow
            // leaves the start of the block unrealised, and an unrealised label counts as absent.
            view.Follower.StickToBottom = false;
            scroll.Offset = scroll.Offset.WithY(0);
            await _SettleAsync(window, TimeSpan.FromMilliseconds(500));

            var topFrames = _CodeFrames(view.TranscriptItems);
            Assert.True(topFrames.Count >= 2, $"only {topFrames.Count} fragment(s) realised at the top of the block");

            var labels = view.TranscriptItems.GetVisualDescendants()
                .OfType<TextBlock>()
                .Count(text => text.Text == "csharp");
            Assert.True(
                labels == 1,
                $"{labels} language labels drawn across {topFrames.Count} realised fragments of one block: the "
                + "label belongs to the fragment that opened the fence and to no other");

            window.Close();
        });
    }

    [Fact]
    public async Task CopyingAFragmentOfASplitCodeBlock_GivesTheWholeBlock()
    {
        await HeadlessAvalonia.RunAsync(() =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            _StreamACodeBlock(vm);

            var spanRows = vm.Transcript.Where(row => row.StartsInsideCodeBlock || row.EndsInsideCodeBlock).ToList();
            Assert.True(spanRows.Count >= 2, $"the block landed in {spanRows.Count} row(s): it was never split");

            // Every fragment hands over the same whole block, not the part under the button. Asserted from each
            // of them: a fix that only taught the last row this would still put half on the clipboard from the first.
            foreach (var row in spanRows)
            {
                var copied = row.SpannedCodeText;
                Assert.True(
                    copied.Contains($"line0 ", StringComparison.Ordinal),
                    $"a fragment copied {copied.Length} characters without the block's first line: "
                    + "the clipboard got part of the block");
                Assert.True(
                    copied.Contains($"line{CodeLines - 1} ", StringComparison.Ordinal),
                    $"a fragment copied {copied.Length} characters without the block's last line: "
                    + "the clipboard got part of the block");
                Assert.True(
                    copied.Length > row.Text.Length,
                    $"a fragment copied {copied.Length} characters against its own {row.Text.Length}: "
                    + "it handed over its own text rather than the block's");
                Assert.False(
                    copied.Contains("```", StringComparison.Ordinal),
                    "the copied text carries a fence: it is the markdown source rather than the code");
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ARowWithNoCodeBlockInIt_KeepsTheChromeItAlwaysHad()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            vm.Apply(new AssistantTextDelta
            {
                SessionId = "S1",
                BlockIndex = 0,
                Text = "an answer with no code in it at all, wrapping over a line or two.\n\n",
            });

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            await _SettleAsync(window, TimeSpan.FromMilliseconds(400));

            // The row root's padding moved from an inline value to the style so a joined-edge class could
            // override it at all — an inline one is a local value and would win. Every row that is not part of
            // a split block has to come out of that move with exactly the chrome it had.
            var root = view.TranscriptItems.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Classes.Contains("rowRoot"));

            Assert.Equal(new Thickness(8, 5), root.Padding);
            Assert.Equal(new Thickness(0, 2), root.Margin);
            Assert.DoesNotContain("codeJoinedAbove", root.Classes);
            Assert.DoesNotContain("codeJoinedBelow", root.Classes);

            var row = vm.Transcript[0];
            Assert.False(row.StartsInsideCodeBlock);
            Assert.False(row.EndsInsideCodeBlock);
            Assert.Equal(4, row.RowContentSpacing);
            Assert.Equal(string.Empty, row.SpannedCodeText);

            window.Close();
        });
    }
}
