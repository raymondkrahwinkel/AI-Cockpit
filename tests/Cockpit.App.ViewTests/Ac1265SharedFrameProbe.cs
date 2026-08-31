using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Avalonia.Media.Imaging;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

// AC-1265 scratch probe. Not a claim: it measures whether a code block could span two rows and always passes.
[Collection("avalonia")]
public sealed class Ac1265SharedFrameProbe
{
    private const string ReadingsPath = "/tmp/ac1265/shared.txt";

    private static void _Report(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadingsPath)!);
        File.AppendAllText(ReadingsPath, line + Environment.NewLine);
    }

    private static async Task _PumpAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }

    // The code block's own frame, by MarkdownView's private CodeBlockBorder type. Walking up to the nearest
    // Border finds the ScrollViewer template's instead, which reports the wrong geometry entirely.
    private static Border? _CodeFrame(Visual row) =>
        row.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.GetType().Name == "CodeBlockBorder");

    [Fact]
    public async Task HowMuchSpaceSitsBetweenTwoRowsOfOneReply()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var head = "```csharp\nvar first = ComputeSomething(argument, other);\n```";
            var tail = "```csharp\nvar third = AndAnotherOne(argument, other);\n```";

            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            var first = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, head);
            var second = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, tail) { IsReplyContinuation = true };
            vm.Transcript.Add(first);
            vm.Transcript.Add(second);

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 760, Height = 460 };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(TimeSpan.FromMilliseconds(700));

            var rowA = view.TranscriptItems.ContainerFromIndex(0)!;
            var rowB = view.TranscriptItems.ContainerFromIndex(1)!;
            var scroll = view.TranscriptScroll!;

            var topA = rowA.TranslatePoint(default, scroll)!.Value.Y;
            var topB = rowB.TranslatePoint(default, scroll)!.Value.Y;
            var bottomA = topA + rowA.Bounds.Height;

            _Report($"row gap: row A {topA:F0}..{bottomA:F0}, row B starts {topB:F0} -> gap {topB - bottomA:F0}px");

            var frameA = _CodeFrame(rowA);
            var frameB = _CodeFrame(rowB);
            if (frameA is null || frameB is null)
            {
                _Report("code frame: not found -- the walk is wrong, nothing below means anything");

                window.Close();

                return;
            }

            var frameTopA = frameA.TranslatePoint(default, scroll)!.Value.Y;
            var frameTopB = frameB.TranslatePoint(default, scroll)!.Value.Y;
            var frameBottomA = frameTopA + frameA.Bounds.Height;

            _Report(
                $"code frame: A {frameTopA:F0}..{frameBottomA:F0}, B starts {frameTopB:F0} "
                + $"-> gap between the two boxes {frameTopB - frameBottomA:F0}px");
            _Report(
                $"inside the row: A's frame sits {frameTopA - topA:F0}px below its row's top and "
                + $"{bottomA - frameBottomA:F0}px above its row's bottom; B's sits {frameTopB - topB:F0}px below its own");
            _Report(
                $"frame margin A={frameA.Margin} padding={frameA.Padding} corner={frameA.CornerRadius} "
                + $"borderThickness={frameA.BorderThickness}");

            // What actually occupies the 61px, so "the padding can go" is a reading rather than a hope.
            foreach (var (label, row, frame) in new[] { ("A", rowA, frameA), ("B", rowB, frameB) })
            {
                var frameBottom = frame.TranslatePoint(default, scroll)!.Value.Y + frame.Bounds.Height;
                var rowTop = row.TranslatePoint(default, scroll)!.Value.Y;
                foreach (var child in row.GetVisualDescendants().OfType<Control>())
                {
                    if (child.Bounds.Height <= 0 || ReferenceEquals(child, frame))
                    {
                        continue;
                    }

                    var top = child.TranslatePoint(default, scroll)?.Y;
                    if (top is null || top < frameBottom - 1 || top > frameBottom + 70)
                    {
                        continue;
                    }

                    _Report(
                        $"  below {label}'s box at +{top - frameBottom:F0}px: {child.GetType().Name} "
                        + $"h={child.Bounds.Height:F0} margin={child.Margin} name={child.Name ?? "-"}");
                }

                _Report($"  row {label} spans {rowTop:F0}..{rowTop + row.Bounds.Height:F0}, its box ends at {frameBottom:F0}");
            }

            window.Close();
        });
    }

    // Can a row reach the whole reply's text? That is what a copy button spanning two rows would need, and the
    // view model already concatenates it for the row-level copy -- this checks it is actually populated.
    [Fact]
    public async Task CanARowReachTheWholeReplysText()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();

            // Through the real path, so _OpenAssistantRow is what wires ReplyRows rather than the probe.
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "First block.\n\n" });
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Second block.\n\n" });
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Third block.\n\n" });

            await _PumpAsync(TimeSpan.FromMilliseconds(200));

            _Report($"reply rows: transcript has {vm.Transcript.Count} rows from one reply");
            for (var i = 0; i < vm.Transcript.Count; i++)
            {
                var row = vm.Transcript[i];
                _Report(
                    $"  row {i}: continuation={row.IsReplyContinuation} "
                    + $"ownText=\"{row.Text.Replace("\n", "\\n")}\" "
                    + $"wholeReply=\"{row.ReplyTextWithImageSuffix.Replace("\n", "\\n")}\"");
            }
        });
    }

    // Does a continuation fence opened without a language drop the label? _CodeBlock only draws it when the
    // language is non-empty, so this is looking at what that reads as rather than trusting the branch.
    [Theory]
    [InlineData("with-language")]
    [InlineData("no-language")]
    public async Task DoesAContinuationFenceDropItsLabel(string shape)
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            vm.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText,
                "```csharp\nvar first = ComputeSomething(argument, other);\n```"));
            var fence = shape == "no-language" ? "```" : "```csharp";
            vm.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText,
                fence + "\nvar third = AndAnotherOne(argument, other);\n```") { IsReplyContinuation = true });

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 760, Height = 460 };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(TimeSpan.FromMilliseconds(700));

            var labels = view.TranscriptItems.GetVisualDescendants()
                .OfType<TextBlock>()
                .Count(t => t.Text == "csharp");
            _Report($"label/{shape}: {labels} language label(s) drawn across the two rows");

            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                Directory.CreateDirectory("/tmp/ac1265");
                frame.Save($"/tmp/ac1265/label-{shape}.png", PngBitmapEncoderOptions.Default);
            }

            window.Close();
        });
    }
}
