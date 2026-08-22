using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// An agent working at the Focus reading level: runs of auto tool calls fold behind an anchor, so the newest row is
/// very often one the level hides. Following a hidden row cannot terminate — it has no height to bring into view,
/// so the follow is never satisfied, asks again on the next scroll change, and each ask realises the row and its
/// template afresh. Read off a hung session's stacks as ScrollIntoView → Measure → ApplyTemplate → styling, on
/// repeat; the pane stops responding and never comes back. Developer never showed it, because there every row is
/// visible and the follow converges at once.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptFocusFoldFollowTests
{
    private const string Prose =
        "## What I found\n\nTwo faults that multiply each other.\n\n" +
        "- `release.yml` builds **only the desktop client**\n- a `Dockerfile` but **no workflow**\n\n";

    private static readonly string ToolOutput = string.Join('\n',
        Enumerable.Range(0, 60).Select(i => $"  line {i}: some output a tool produced, of the length tools produce"));

    [Fact]
    public async Task AFoldedRunOfToolCalls_DoesNotStallTheTranscript()
    {
        // The failure is a stall, so the assertion is a deadline: without the fix this scenario never reaches the
        // second turn, and a test that simply awaited it would hang a CI run rather than fail it.
        var run = HeadlessAvalonia.RunAsync(_StreamSixAgentTurns);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90)));

        Assert.True(
            ReferenceEquals(finished, run),
            "six agent turns at the Focus reading level did not finish inside 90 seconds: the transcript is stalling again");

        await run;
    }

    private static async Task _StreamSixAgentTurns()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();
        vm.ReadingLevel = ReadingLevel.Focus;

        var view = new SessionView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        window.UpdateLayout();
        await Task.Delay(200);

        var tool = 0;
        for (var turn = 0; turn < 6; turn++)
        {
            foreach (var chunk in _Chunks(Prose))
            {
                vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = chunk });
                await Task.Delay(1);
            }

            // Two or more consecutive auto tool calls are what forms a fold group, and the group is what hides rows.
            for (var t = 0; t < 6; t++)
            {
                var id = $"tool-{tool++}";
                vm.Apply(new ToolUseRequested
                {
                    SessionId = "S1", ToolUseId = id, ToolName = "Bash", InputJson = "{\"command\":\"dotnet test\"}",
                });
                await Task.Delay(3);
                vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = id, Content = ToolOutput, IsError = false });
                await Task.Delay(3);
            }

            vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

            // Pace every turn but the last: vm.Apply mutates Transcript synchronously, so nothing downstream of the
            // final turn needs time to catch up before the count below is read — only the streaming cadence
            // in between turns (what the fold-group/ScrollIntoView reentrancy this test guards needs to see) does.
            if (turn < 5)
            {
                await Task.Delay(120);
            }
        }

        Assert.Equal(42, vm.Transcript.Count);
    }

    private static IEnumerable<string> _Chunks(string text)
    {
        for (var i = 0; i < text.Length; i += 12)
        {
            yield return text.Substring(i, Math.Min(12, text.Length - i));
        }
    }
}
