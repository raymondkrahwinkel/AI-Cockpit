using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-442's route decision, measured rather than argued (a repeatable ruler, not a pass/fail test, like
/// <see cref="TerminalRenderAllocationBenchmark"/>). Six sessions as miniatures, drawn every frame against
/// drawn at <see cref="SnapshotHz"/> and blitted, on identical output so only the drawing differs. Software
/// rasterisation: the CPU-side cost, which for a terminal is what scales with pane count.
/// </summary>
[Collection("avalonia")]
public class MiniatureFrameCostBenchmark
{
    private const int Sessions = 6;
    private const double FullWidth = 1000;
    private const double FullHeight = 640;
    private const double Scale = 0.28;

    private const int FrameRate = 60;
    private const int SnapshotHz = 2;
    private const int WarmupFrames = 30;
    private const int MeasuredFrames = 180;

    public static readonly string ResultFile =
        Path.Combine(Path.GetTempPath(), "cockpit-miniature-frame-cost.txt");

    [Fact]
    public async Task MeasureBothRoutes()
    {
        Sample live = default, snapshot = default;

        await HeadlessAvalonia.RunAsync(() =>
        {
            live = Measure(snapshotted: false);
            snapshot = Measure(snapshotted: true);
            return Task.CompletedTask;
        });

        var report =
            $"{Sessions} miniatures @ scale {Scale}, {FullWidth}x{FullHeight} panes, {FrameRate}fps budget "
            + $"({1000.0 / FrameRate:F2} ms/frame)\n"
            + $"live     : {live}\n"
            + $"snapshot : {snapshot} (refresh {SnapshotHz} Hz)\n";
        File.WriteAllText(ResultFile, report);

        Assert.True(live.MsPerFrame > 0 && snapshot.MsPerFrame > 0,
            $"neither route measured anything:\n{report}");
        Assert.True(
            live.MsPerFrame > snapshot.MsPerFrame,
            "expected snapshots to cost less per frame than live rendering");
        Assert.Equal(0, live.CacheBytes);
        // RenderTargetBitmap uses four bytes per pixel.
        Assert.Equal((long)(FullWidth * Scale) * (long)(FullHeight * Scale) * 4 * Sessions, snapshot.CacheBytes);
    }

    private static Sample Measure(bool snapshotted)
    {
        var terminals = new TerminalControl[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            terminals[i] = new TerminalControl { FontSize = 14, UseLayoutRounding = false };
            var size = new Size(FullWidth, FullHeight);
            terminals[i].Measure(size);
            terminals[i].Arrange(new Rect(size));

            // A screen's worth of scrollback before measuring, so no frame is drawing a half-empty grid.
            for (var line = 0; line < terminals[i].Buffer.Rows; line++)
            {
                terminals[i].Write(Line(i, line));
            }
        }

        var tile = new PixelSize((int)(FullWidth * Scale), (int)(FullHeight * Scale));

        // The rail: the six tiles stacked, which is the surface a frame actually has to produce.
        var rail = new RenderTargetBitmap(new PixelSize(tile.Width, tile.Height * Sessions));
        var tiles = snapshotted
            ? Enumerable.Range(0, Sessions).Select(_ => new RenderTargetBitmap(tile)).ToArray()
            : [];

        // Every frame writes to every session either way — the pty does not stop for a route.
        void Pump(int frame)
        {
            for (var i = 0; i < Sessions; i++)
            {
                terminals[i].Write(Line(i, frame));
            }
        }

        void DrawScaled(DrawingContext ctx, TerminalControl terminal)
        {
            using (ctx.PushTransform(Matrix.CreateScale(Scale, Scale)))
            {
                terminal.Render(ctx);
            }
        }

        void RenderFrame(int frame)
        {
            Pump(frame);

            if (snapshotted && frame % (FrameRate / SnapshotHz) == 0)
            {
                for (var i = 0; i < Sessions; i++)
                {
                    using var ctx = tiles[i].CreateDrawingContext();
                    DrawScaled(ctx, terminals[i]);
                }
            }

            using var railCtx = rail.CreateDrawingContext();
            for (var i = 0; i < Sessions; i++)
            {
                var slot = new Rect(0, i * tile.Height, tile.Width, tile.Height);
                if (snapshotted)
                {
                    railCtx.DrawImage(tiles[i], slot);
                }
                else
                {
                    using (railCtx.PushTransform(Matrix.CreateTranslation(0, i * tile.Height)))
                    {
                        DrawScaled(railCtx, terminals[i]);
                    }
                }
            }
        }

        for (var f = 0; f < WarmupFrames; f++)
        {
            RenderFrame(f);
        }

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (var f = 0; f < MeasuredFrames; f++)
        {
            RenderFrame(WarmupFrames + f);
        }
        clock.Stop();
        var bytesAfter = GC.GetAllocatedBytesForCurrentThread();

        rail.Dispose();
        foreach (var bitmap in tiles)
        {
            bitmap.Dispose();
        }

        return new Sample(
            clock.Elapsed.TotalMilliseconds / MeasuredFrames,
            (bytesAfter - bytesBefore) / (double)MeasuredFrames,
            tile.Width * tile.Height * 4L * tiles.Length);
    }

    // Deterministic per-session, per-frame output: a pure function of both indices, so every run and both
    // routes process byte-identical input.
    private static byte[] Line(int session, int frame)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n\x1b[").Append(31 + ((session + frame) % 7)).Append('m');
        sb.Append('[').Append(session).Append(':').Append(frame.ToString().PadLeft(5)).Append("] ");
        sb.Append("the quick brown fox jumps over the lazy dog ");
        sb.Append("word").Append(frame % 13).Append(" value").Append((frame * 7) % 97);
        sb.Append("\x1b[0m");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private readonly record struct Sample(double MsPerFrame, double BytesPerFrame, long CacheBytes)
    {
        public override string ToString() =>
            $"{MsPerFrame:F2} ms/frame, {BytesPerFrame / 1024:F0} KB/frame allocated"
            + (CacheBytes > 0 ? $", {CacheBytes / 1024.0 / 1024.0:F1} MB of cached tiles" : "");
    }
}
