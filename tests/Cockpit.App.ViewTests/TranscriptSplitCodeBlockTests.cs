using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.App.Composition;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1265: a fenced code block carries no blank line for AC-1238's split to find, so it used to grow as one
/// row far past the viewport — the shape that makes the virtualising panel re-anchor its average on that one
/// row, which is the scrollbar thumb jumping between lengths frame on frame. It is split on a line boundary
/// now, and Copy must still hand over the whole block rather than the fragment the button sits under.
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
                Assert.DoesNotContain("```", copied, StringComparison.Ordinal);
            }

            return Task.CompletedTask;
        });
    }
}
