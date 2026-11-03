using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.App.Composition;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1271: AC-1238 ends a row at the blank line that finishes a markdown block and AC-1265 bounds one inside
/// a fence, which leaves every block with neither. Measured: a tight bullet list grew to 815-1415px and one
/// paragraph without a blank line to 512-1141px, in viewports of 461 and 382px, and on 8-16% of painted frames
/// the panel drew another part of the transcript. AC-1272: a markdown table has the same shape.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptUnbrokenBlockSplitTests
{
    // AC-1272: a table-shaped header+separator pasted inside a still-open fence (e.g. an example the reply is
    // showing) must not be read as a real table -- `_OpenTableLineEnd` needs the same open-fence guard
    // `_UnbrokenBlockEnd` already has, or it seals the row mid-fence and abandons the fence's own continuation.
    [Fact]
    public async Task TableLikeLinesInsideAnOpenFence_DoNotSplitTheFenceAsATable()
    {
        var rows = 0;
        var sawTableSplit = false;
        var text = string.Empty;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();

            // Three chunks, not one: `_OpenBlockIsTable` only ever looks at what already streamed in, so the
            // header+separator must land in the row before the chunk that would tip it over TableFragmentLines.
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "```text\ncode line\n\n" });
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "| a | b |\n|---|---|\n" });
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "| c | d |\n| e | f |\n" });

            rows = vm.Transcript.Count;
            sawTableSplit = vm.Transcript.Any(row => row.StartsInsideTable || row.EndsInsideTable);
            text = string.Concat(vm.Transcript.Select(row => row.Text));
        });

        Assert.Equal(1, rows);
        Assert.False(sawTableSplit, "a table-shaped example inside a still-open fence was split as if it were a real table");
        Assert.Equal("```text\ncode line\n\n| a | b |\n|---|---|\n| c | d |\n| e | f |\n", text);
    }
}
