using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-57/AC-61 measurement harness (not a pass/fail test — a repeatable ruler). Renders a terminal headlessly for
/// a fixed number of frames and reports managed allocation per render frame, so each renderer change (cache size,
/// FormattedText→GlyphRun) is comparable against the same deterministic workload. Two scenarios localise the cost:
/// <list type="bullet">
/// <item><b>streaming</b> — a new unique coloured line each frame (one cache miss + a full grid of draws), the
/// realistic heavy-output case that drives AC-57.</item>
/// <item><b>cache-hit</b> — the same line every frame (zero misses), isolating the pure per-row draw overhead
/// from the text-shaping a miss pays.</item>
/// </list>
/// Content is a pure function of the index, so runs process byte-identical input and only the renderer differs.
/// Results are written to a temp file the driver reads.
/// </summary>
[Collection("avalonia")]
public class TerminalRenderAllocationBenchmark
{
    private const int Cols = 160;
    private const int Rows = 50;
    private const int WarmupFrames = 40;
    private const int MeasuredFrames = 300;

    public static readonly string ResultFile =
        Path.Combine(Path.GetTempPath(), "cockpit-bench-result.txt");

    [Fact]
    public void MeasureRenderAllocation()
    {
        double streaming = 0, cacheHit = 0;

        HeadlessAvalonia.Run(() =>
        {
            streaming = Measure(StreamingLine);
            cacheHit = Measure(_ => ConstantLine);
        });

        File.WriteAllText(ResultFile,
            $"streaming: {streaming:F0} bytes/frame\ncache-hit: {cacheHit:F0} bytes/frame");
        Assert.True(streaming > 0 && cacheHit > 0, "expected the render path to allocate something measurable");
    }

    private static double Measure(Func<int, byte[]> lineFor)
    {
        var term = new TerminalControl { FontSize = 14 };
        var size = new Size(Cols * 9, Rows * 18);
        term.Measure(size);
        term.Arrange(new Rect(size));

        for (int i = 0; i < Rows; i++)
        {
            term.Write(lineFor(i));
        }

        var target = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));

        void RenderFrame(int frame)
        {
            term.Write(lineFor(frame));
            using var ctx = target.CreateDrawingContext();
            term.Render(ctx);
        }

        for (int f = 0; f < WarmupFrames; f++)
        {
            RenderFrame(Rows + f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < MeasuredFrames; f++)
        {
            RenderFrame(Rows + WarmupFrames + f);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        return (after - before) / (double)MeasuredFrames;
    }

    // Deterministic per-frame content: varied text (so runs differ) plus a rotating SGR colour, a pure function
    // of the index for byte-identical input across runs. Each frame's line is unique → one cache miss per frame.
    private static byte[] StreamingLine(int i)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n");
        sb.Append("\x1b[").Append(31 + (i % 7)).Append('m');
        sb.Append('[').Append(i.ToString().PadLeft(6)).Append("] ");
        sb.Append("the quick brown fox jumps over the lazy dog ");
        sb.Append("word").Append(i % 13).Append(" value").Append((i * 7) % 97).Append(" tail").Append(i % 5);
        sb.Append("\x1b[0m");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // The same line every frame → after warm-up every visible run is a cache hit, so this measures only the
    // per-row draw cost, with no text-shaping.
    private static readonly byte[] ConstantLine =
        Encoding.UTF8.GetBytes("\r\n\x1b[32mthe quick brown fox jumps over the lazy dog word value tail\x1b[0m");
}
