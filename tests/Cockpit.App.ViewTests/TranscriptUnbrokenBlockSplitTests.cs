using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1271: AC-1238 ends a row at the blank line that finishes a markdown block and AC-1265 bounds one inside
/// a fence, which leaves every block with neither. Measured: a tight bullet list grew to 815-1415px and one
/// paragraph without a blank line to 512-1141px, in viewports of 461 and 382px, and on 8-16% of painted frames
/// the panel drew another part of the transcript. Prose and fenced code ride along as the negative control.
/// AC-1272: a markdown table has the same shape, left out of AC-1271 on purpose (route A2, see below).
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptUnbrokenBlockSplitTests
{
    private const int Chunks = 40;

    /// <summary>Fewer painted frames than this cannot say anything about movement between them.</summary>
    private const int MinimumFrames = 30;

    /// <summary>What a split's own seam costs, measured: one frame, against 9-20 for an unbounded block.</summary>
    private const int SeamFrames = 2;

    private sealed record Reading(double TallestRow, double Viewport, double MaxExtent, int ReplyNowhere, int Frames);

    [Theory]
    [InlineData("session", 900, 600)]
    [InlineData("chat", 420, 560)]
    public async Task ABlockWithNoBlankLineInIt_NeverBecomesTheRowThePanelReAnchorsOn(string view, double width, double height)
    {
        var list = await _StreamAsync(view, width, height, i => $"- bullet {i} of a tight list that keeps growing and wrapping over several lines of text.\n");
        var paragraph = await _StreamAsync(view, width, height, i => $"sentence {i} of one very long paragraph that never contains a blank line and just keeps going. ");
        var prose = await _StreamAsync(view, width, height, i => $"paragraph {i} of a long markdown answer that keeps growing and wrapping over several lines.\n\n");
        var code = await _StreamAsync(view, width, height, i => i == 0
            ? "```csharp\n"
            : $"    var line{i} = ComputeSomething({i}, \"a fairly long argument so the line wraps\");\n");
        var table = await _StreamAsync(view, width, height, i => i == 0
            ? "| col a | col b |\n|---|---|\n"
            : $"| row {i} | value {i} with a bit of extra text so the row is not trivially short |\n");

        // Gates first, each closing a way this could pass while the behaviour is broken.
        foreach (var (name, run) in new[] { ("list", list), ("paragraph", paragraph), ("prose", prose), ("code", code), ("table", table) })
        {
            Assert.True(
                run.Frames >= MinimumFrames,
                $"the {name} run painted only {run.Frames} frames, under the {MinimumFrames} this judgement needs");
            Assert.True(
                run.MaxExtent > run.Viewport * 3,
                $"the {name} run never grew past {run.MaxExtent:F0}px in a {run.Viewport:F0}px viewport: too "
                + "little of it was ever unrealised for the panel to be estimating anything");
        }

        // Judged together, so a change that trades one shape for another cannot report only the half that held.
        var faults = new List<string>();
        foreach (var (name, run, before) in new[]
        {
            ("tight bullet list", list, "815px at 900x600 and 1415px at 420x560"),
            ("unbroken paragraph", paragraph, "512px at 900x600 and 1141px at 420x560"),
            ("markdown table", table, "1261px at both, 13 of 93 frames at 900x600 and 20 of 113 at 420x560 (AC-1271)"),
            ("prose", prose, "151px and 261px, already within the viewport"),
            ("fenced code block", code, "386px, already within the viewport since AC-1265"),
        })
        {
            if ((name is "prose" or "fenced code block") && run.ReplyNowhere > 0)
            {
                faults.Add(
                    $"the {name} control lost the end of the reply on {run.ReplyNowhere} of {run.Frames} frames, "
                    + "where it measured zero before this change — the change was bought with a control");
            }

            // A fifth of the viewport of slack: AC-1265 left a code row at 386px in a 382px viewport and that
            // one measured zero on every reading, so fitting exactly is not what makes a row the outlier.
            if (run.TallestRow > run.Viewport * 1.2)
            {
                faults.Add(
                    $"the tallest row of the {name} reached {run.TallestRow:F0}px in a {run.Viewport:F0}px "
                    + $"viewport (before this change: {before}) — one row the panel's average is the whole of");
            }

            // Two frames of slack, not zero: with the bound in place this measures one frame at the seam of a
            // split, against 9-20 without it. Prose and code reach zero and are held to it below.
            if (run.ReplyNowhere > SeamFrames)
            {
                faults.Add(
                    $"{run.ReplyNowhere} of {run.Frames} painted frames of the {name} had no end of the reply on "
                    + "screen at all (before this change: 9-20 for the list, 2-18 for the paragraph, 0 for both "
                    + "controls) — the panel was drawing another part of the transcript while the follow was on "
                    + "the reply");
            }
        }

        Assert.True(faults.Count == 0, string.Join("; and ", faults));
    }

    [Fact]
    public async Task AnOrderedListSplitAcrossRows_KeepsCountingWhereTheRowBeforeItStopped()
    {
        var markers = new List<string>();
        var rows = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 1; i <= Chunks; i++)
            {
                vm.Apply(new AssistantTextDelta
                {
                    SessionId = "S1",
                    BlockIndex = 0,
                    Text = $"{i}. step {i} of a numbered list that runs on well past one row's worth of markdown.\n",
                });
            }

            rows = vm.Transcript.Count;

            // The last row's own markdown, rendered the way the transcript renders it.
            var markdown = new MarkdownView { Markdown = vm.Transcript[^1].Text };
            var window = new Window { Content = markdown, Width = 900, Height = 600 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(100);

            markers.AddRange(markdown.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty)
                .Where(text => text.EndsWith('.') && int.TryParse(text[..^1], out _)));

            window.Close();
        });

        Assert.True(rows > 1, $"the numbered list stayed in {rows} row(s), so nothing was split and this proves nothing");
        Assert.NotEmpty(markers);
        Assert.NotEqual("1.", markers[0]);
    }

    // AC-1272, route A2: column 1 is short in the first half (`r0`..`r24`) and much longer in the second, so a
    // fragment measuring only its own rows comes out narrower. A tall window keeps every fragment realised,
    // exercising TableSpanRevision: fragment 1 seals and first renders before the wide cells even exist.
    [Fact]
    public async Task ATableSplitAcrossFragments_KeepsColumnWidthsEqualAcrossFragments()
    {
        var fragmentCount = 0;
        var fellBackToProse = false;
        var widths = new List<double>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            var (window, items, _) = await _OpenAsync("session", vm, 900, 8000);

            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "| col a | col b |\n|---|---|\n" });
            await _PumpAsync(null, TimeSpan.FromMilliseconds(25));

            for (var i = 0; i < 50; i++)
            {
                var cell = i < 25 ? $"r{i}" : $"row {i} with a noticeably longer label than the first half used";
                vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = $"| {cell} | v{i} |\n" });
                await _PumpAsync(null, TimeSpan.FromMilliseconds(20));
            }

            await _PumpAsync(null, TimeSpan.FromMilliseconds(400));

            for (var i = 0; i < vm.Transcript.Count; i++)
            {
                var row = vm.Transcript[i];
                if (row.Kind != TranscriptEntryKind.AssistantText || !(row.StartsInsideTable || row.EndsInsideTable))
                {
                    continue;
                }

                fragmentCount++;
                if (items.ContainerFromIndex(i) is not { } container)
                {
                    continue; // window too short after all -- nothing realised to measure for this one
                }

                var grid = container.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count > 1);
                if (grid is null)
                {
                    fellBackToProse = true;
                    continue;
                }

                widths.Add(grid.ColumnDefinitions[0].Width.Value);
            }

            window.Close();
        });

        Assert.True(fragmentCount >= 2, $"the table stayed in {fragmentCount} fragment(s), so nothing was split and this proves nothing");
        Assert.False(fellBackToProse, "a fragment of the split table fell back to prose instead of a table");
        Assert.True(widths.Count >= 2, $"only {widths.Count} of {fragmentCount} fragments were realised in an 8000px window -- proves nothing");
        Assert.True(
            widths.Distinct().Count() == 1,
            $"column 1 measured {string.Join(", ", widths)}px across fragments instead of one shared width");
    }

    private static async Task<Reading> _StreamAsync(string view, double width, double height, Func<int, string> chunk)
    {
        var tallest = 0.0;
        var maxExtent = 0.0;
        var viewport = 0.0;
        var nowhere = 0;
        var frames = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 12; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText,
                    string.Join(' ', Enumerable.Repeat($"filler row {i} with enough words to wrap a few times", 12))));
            }

            // Through the session's own event path, not by appending to a row the test made itself: the split
            // being measured lives there, and AppendText is a seam the app never uses (AC-1238).
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "start of the reply.\n\n" });

            var (window, items, scroll) = await _OpenAsync(view, vm, width, height);

            void Sample()
            {
                frames++;
                maxExtent = Math.Max(maxExtent, scroll.Extent.Height);
                for (var i = 0; i < items.ItemCount; i++)
                {
                    if (items.ContainerFromIndex(i) is { } container)
                    {
                        tallest = Math.Max(tallest, container.Bounds.Height);
                    }
                }

                // Not frames merely missing the newest row — that one is always a frame below the fold before
                // the follow reaches it, so counting those would measure physics rather than a defect.
                var last = items.ItemCount - 1;
                if (items.ContainerFromIndex(last) is null && (last < 1 || items.ContainerFromIndex(last - 1) is null))
                {
                    nowhere++;
                }
            }

            for (var i = 0; i < Chunks; i++)
            {
                vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = chunk(i) });
                await _PumpAsync(Sample, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, TimeSpan.FromMilliseconds(400));
            viewport = scroll.Viewport.Height;
            window.Close();
        });

        return new Reading(tallest, viewport, maxExtent, nowhere, frames);
    }

    // The session panel or the assistant chat window, each hosting the same vm through its own transcript
    // ItemsControl/ScrollViewer — the shared setup `_StreamAsync` and the table tests below both need.
    private static async Task<(Window Window, ItemsControl Items, ScrollViewer Scroll)> _OpenAsync(
        string view, SessionViewModel vm, double width, double height)
    {
        Window window;
        ItemsControl items;
        ScrollViewer scroll;

        if (view == "session")
        {
            var sessionView = new SessionView { DataContext = vm };
            window = new Window { Content = sessionView, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(300));
            items = sessionView.TranscriptItems;
            scroll = sessionView.TranscriptScroll!;
        }
        else
        {
            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(vm);
            var chat = new AssistantChatWindow
            {
                Width = width,
                Height = height,
                DataContext = new AssistantChatViewModel(
                    host,
                    Substitute.For<IAssistantSettingsStore>(),
                    Substitute.For<IVoicePlaybackQueue>()),
            };
            chat.Show();
            chat.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(300));
            window = chat;
            items = chat.ChatView.TranscriptItems;
            scroll = chat.ChatView.TranscriptScroll!;
        }

        return (window, items, scroll);
    }

    /// <summary>One sample per forced render tick: the transcript as it is about to be painted.</summary>
    private static async Task _PumpAsync(Action? sample, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            sample?.Invoke();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }
}
