using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

// AC-1265 scratch probe. Not a claim: it renders the split block beside the one-row shape it replaces and
// counts what differs, so a change outside the code block shows up as a number. Always passes.
[Collection("avalonia")]
public sealed class Ac1265RenderDiffProbe
{
    private const string ReadingsPath = "/tmp/ac1265/renderdiff.txt";

    private static void _Report(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadingsPath)!);
        File.AppendAllText(ReadingsPath, line + Environment.NewLine);
    }

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

    private static byte[] _Pixels(WriteableBitmap bitmap, out int stride, out int height)
    {
        using var buffer = bitmap.Lock();
        stride = buffer.RowBytes;
        height = buffer.Size.Height;
        var bytes = new byte[stride * height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        return bytes;
    }

    // Rows of the two frames that differ, and the band they fall in — a difference confined to the code block
    // is the change asked for; one anywhere else is the thing this probe exists to catch.
    private static (int Pixels, int FirstRow, int LastRow, int FirstColumn, int LastColumn) _Diff(WriteableBitmap a, WriteableBitmap b)
    {
        var left = _Pixels(a, out var stride, out var height);
        var right = _Pixels(b, out var otherStride, out var otherHeight);
        if (stride != otherStride || height != otherHeight)
        {
            return (-1, -1, -1, -1, -1);
        }

        var differing = 0;
        var first = -1;
        var last = -1;
        var firstColumn = int.MaxValue;
        var lastColumn = -1;
        for (var y = 0; y < height; y++)
        {
            var rowDiffers = false;
            for (var x = 0; x < stride; x += 4)
            {
                var i = (y * stride) + x;
                if (left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2])
                {
                    differing++;
                    rowDiffers = true;
                    firstColumn = Math.Min(firstColumn, x / 4);
                    lastColumn = Math.Max(lastColumn, x / 4);
                }
            }

            if (rowDiffers)
            {
                first = first < 0 ? y : first;
                last = y;
            }
        }

        return (differing, first, last, firstColumn == int.MaxValue ? -1 : firstColumn, lastColumn);
    }

    private static void _WriteMask(WriteableBitmap a, WriteableBitmap b, string path)
    {
        var left = _Pixels(a, out var stride, out var height);
        var right = _Pixels(b, out _, out _);
        var mask = new WriteableBitmap(
            new Avalonia.PixelSize(stride / 4, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888);
        var bytes = new byte[stride * height];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var differs = left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2];
            bytes[i] = differs ? (byte)0 : (byte)0;
            bytes[i + 1] = differs ? (byte)0 : (byte)0;
            bytes[i + 2] = differs ? (byte)255 : (byte)0;
            bytes[i + 3] = 255;
        }

        using (var buffer = mask.Lock())
        {
            Marshal.Copy(bytes, 0, buffer.Address, bytes.Length);
        }

        mask.Save(path, PngBitmapEncoderOptions.Default);
    }

    // The count split by where it falls: the scrollbar is the change this ticket is about, so it has to be
    // counted apart from everything else rather than folded into one total that reads as damage.
    private static void _ReportBands(WriteableBitmap a, WriteableBitmap b)
    {
        var left = _Pixels(a, out var stride, out var height);
        var right = _Pixels(b, out _, out _);
        var width = stride / 4;
        var scrollbar = 0;
        var body = 0;
        var bottomFold = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                if (left[i] == right[i] && left[i + 1] == right[i + 1] && left[i + 2] == right[i + 2])
                {
                    continue;
                }

                if (x >= width - 20)
                {
                    scrollbar++;
                }
                else if (y >= 545)
                {
                    // Below this the viewport clips the block, so the two runs are showing different code
                    // lines rather than the same ones drawn differently.
                    bottomFold++;
                }
                else
                {
                    body++;
                }
            }
        }

        _Report($"  of which: scrollbar column {scrollbar}, below the fold at row 545 {bottomFold}, the block's own body {body}");
    }

    private static void _StreamACodeBlock(SessionViewModel vm, int lines)
    {
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here it is:\n\n```csharp\n" });
        for (var i = 0; i < lines; i++)
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

    private static string _WholeFence(int lines) =>
        "```csharp\n"
        + string.Concat(Enumerable.Range(0, lines).Select(i => $"    var line{i} = ComputeSomethingWithARatherLongName(argument{i});\n"))
        + "```\n\n";

    private static async Task<WriteableBitmap?> _RenderAsync(SessionViewModel vm, string name)
    {
        var view = new SessionView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        await _SettleAsync(window, TimeSpan.FromMilliseconds(800));

        view.Follower.StickToBottom = false;
        view.TranscriptScroll!.Offset = view.TranscriptScroll.Offset.WithY(0);
        await _SettleAsync(window, TimeSpan.FromMilliseconds(500));

        var frame = window.CaptureRenderedFrame();
        if (frame is not null)
        {
            Directory.CreateDirectory("/tmp/ac1265");
            frame.Save($"/tmp/ac1265/diff-{name}.png", PngBitmapEncoderOptions.Default);
        }

        window.Close();

        return frame;
    }

    [Fact]
    public async Task WhatTheSplitChangesOnScreen()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            const int Lines = 40;

            // "Before": what main produces for this same stream -- AC-1238 already gives the lead-in paragraph
            // its own row, and only the fence stayed in one. Putting the whole markdown in a single row would
            // price that older split too and report it as this one's doing.
            var before = new SessionViewModel();
            before.Transcript.Clear();
            // IsReplyTail spelled out on both: it defaults to true, so hand-built rows each draw the row action
            // strip -- 28px that _OpenAssistantRow takes off every row but the last, and that would otherwise
            // land in this count as something the split did.
            before.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Here it is:\n\n")
            {
                IsReplyTail = false,
            });
            before.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, _WholeFence(Lines))
            {
                IsReplyContinuation = true,
            });
            var beforeFrame = await _RenderAsync(before, "codeblock-one-row");

            var after = new SessionViewModel();
            after.Transcript.Clear();
            _StreamACodeBlock(after, Lines);
            var afterFrame = await _RenderAsync(after, "codeblock-split");

            if (beforeFrame is null || afterFrame is null)
            {
                _Report("code block: no frame captured");

                return;
            }

            var diff = _Diff(beforeFrame, afterFrame);
            _Report(
                $"code block, one row vs split: {diff.Pixels} pixels differ, rows {diff.FirstRow}..{diff.LastRow} "
                + $"of {beforeFrame.PixelSize.Height}, columns {diff.FirstColumn}..{diff.LastColumn} "
                + $"of {beforeFrame.PixelSize.Width}");

            // The same transcript with no fence in it: nothing about the split applies, so anything differing
            // here is the row chrome having moved for every row rather than for a spanned block.
            var proseA = new SessionViewModel();
            proseA.Transcript.Clear();
            for (var i = 0; i < 6; i++)
            {
                proseA.Apply(new AssistantTextDelta
                {
                    SessionId = "S1",
                    BlockIndex = 0,
                    Text = $"paragraph {i} of an answer with no code in it at all, wrapping over a line or two.\n\n",
                });
            }

            // A mask of the differing pixels, because a count spread over a band says how many and not where.
            _WriteMask(beforeFrame, afterFrame, "/tmp/ac1265/diff-mask.png");
            _ReportBands(beforeFrame, afterFrame);

            var proseFrame = await _RenderAsync(proseA, "prose-only");
            _Report($"prose only: rendered at {proseFrame?.PixelSize.Height ?? 0}px for eyeballing beside the block");
        });
    }
}
