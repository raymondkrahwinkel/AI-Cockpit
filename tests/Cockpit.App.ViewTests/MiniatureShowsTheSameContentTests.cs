using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Headless;
using Cockpit.App.Controls;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-442 #5, on a render rather than on a claim: the miniature of a running session shows that session's
/// content, not an empty box or a stale one. Compared as a band profile — the image split into horizontal
/// bands, each band's mean ink — because the two frames are rasterised at different sizes and only the
/// structure can be compared, which is also all a miniature is read for (activity, shape, colour).
/// <para>
/// The negative control is what makes the number mean anything: a second session with different output has
/// to profile *worse* against the miniature than its own pane does, or the comparison would pass on any two
/// terminals that merely look like terminals.
/// </para>
/// <para>Both PNGs are written to <see cref="OutputDirectory"/> for the eyeball half of the check.</para>
/// </summary>
[Collection("avalonia")]
public class MiniatureShowsTheSameContentTests
{
    private const double FullWidth = 1000;
    private const double FullHeight = 640;
    private const double Scale = 0.28;
    // Coarser than a text row at the miniature's size (39 rows over ~180px), or the profile is comparing
    // rasterisation artefacts: a band finer than a row samples glyph tops against glyph bottoms.
    private const int Bands = 20;

    public static readonly string OutputDirectory =
        Path.Combine(Path.GetTempPath(), "cockpit-ac442-miniature");

    [Fact]
    public async Task TheMiniatureOfARunningSession_ShowsTheSameContentAsTheFullPane()
    {
        double same = 0, different = 0;
        (int Cols, int Rows) fullGrid = default, miniGrid = default;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            Directory.CreateDirectory(OutputDirectory);

            var full = await ProfileAsync(seed: 1, scale: 1.0, "full.png");
            var mini = await ProfileAsync(seed: 1, Scale, "miniature.png");
            var other = await ProfileAsync(seed: 2, Scale, "miniature-other-session.png");

            (fullGrid, miniGrid) = (full.Grid, mini.Grid);
            same = Correlation(full.Bands, mini.Bands);
            different = Correlation(full.Bands, other.Bands);
        });

        // The two panes have to be the same session before their pictures are worth comparing — and this is
        // the invariant itself, seen from the render side. Compared against each other rather than against a
        // written-down grid: the cell size is the machine's font's business, and a container measures a
        // different one.
        Assert.Equal(fullGrid, miniGrid);

        File.WriteAllText(Path.Combine(OutputDirectory, "correlation.txt"),
            $"own pane: {same:F3}\nother session: {different:F3}\n");

        Assert.True(same > 0.9,
            $"the miniature's band profile correlates {same:F3} with its own pane — it is not showing the same content");
        Assert.True(same > different + 0.2,
            $"a miniature of a different session scored {different:F3} against {same:F3}; the comparison is not "
            + $"discriminating between sessions, so the {same:F3} proves nothing");
    }

    /// <summary>The pane shown at <paramref name="scale"/>, saved, and reduced to its band profile.</summary>
    private static async Task<(double[] Bands, (int Cols, int Rows) Grid)> ProfileAsync(
        int seed, double scale, string fileName)
    {
        var terminal = new TerminalControl { FontSize = 14, UseLayoutRounding = false };
        var host = new MiniatureHost { Child = terminal, Scale = scale, UseLayoutRounding = false };
        using var scene = RenderedScene.Show(host, FullWidth * scale, FullHeight * scale);

        // The grid the pane settles on is what the transcript is then written into, at either scale — write
        // first and the 80x24 default is what gets filled, which is a different pane, not a smaller one.
        await SettleAsync(terminal);

        foreach (var line in Transcript(seed))
        {
            terminal.Write(Encoding.UTF8.GetBytes(line));
        }

        scene.Window.UpdateLayout();
        using var frame = scene.Window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless renderer produced no frame");
        frame.Save(Path.Combine(OutputDirectory, fileName));

        using var buffer = frame.Lock();
        var bands = new double[Bands];
        var row = new byte[buffer.RowBytes];
        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;

        for (var y = 0; y < buffer.Size.Height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);

            double ink = 0;
            for (var x = 0; x < buffer.Size.Width; x++)
            {
                var p = x * bytesPerPixel;
                ink += (row[p] + row[p + 1] + row[p + 2]) / 3.0;
            }

            bands[Math.Min(Bands - 1, y * Bands / buffer.Size.Height)] += ink / buffer.Size.Width;
        }

        // Each band summed a different number of source rows at the two sizes; normalise so the profiles
        // compare as shapes rather than as heights.
        var rowsPerBand = buffer.Size.Height / (double)Bands;
        for (var b = 0; b < Bands; b++)
        {
            bands[b] /= rowsPerBand;
        }

        return (bands, (terminal.Buffer.Cols, terminal.Buffer.Rows));
    }

    /// <summary>
    /// Waits for the pane's grid to reach the size its layout implies. On the event rather than on a delay:
    /// until the control's resize debounce fires, the grid sits at the 80x24 default, which a
    /// wait-until-it-stops-moving poll reads as settled and hands back the wrong pane.
    /// </summary>
    private static async Task SettleAsync(TerminalControl terminal)
    {
        var resized = new TaskCompletionSource();
        terminal.Resized += OnResized;
        try
        {
            if (await Task.WhenAny(resized.Task, Task.Delay(2000)) != resized.Task)
            {
                throw new TimeoutException(
                    $"the pane never left its {terminal.Buffer.Cols}x{terminal.Buffer.Rows} default grid");
            }
        }
        finally
        {
            terminal.Resized -= OnResized;
        }

        void OnResized(object? sender, (int Cols, int Rows) e) => resized.TrySetResult();
    }

    // An agent session's shape: coloured tool lines, indented output, blank runs and a prompt — the ink
    // pattern a miniature is read for. Seeded so two sessions differ in content but not in character.
    private static IEnumerable<string> Transcript(int seed)
    {
        yield return "\x1b[2J\x1b[H";
        for (var i = 0; i < 34; i++)
        {
            var n = (i * seed * 7) % 11;
            yield return n switch
            {
                0 or 1 => "\r\n",
                2 or 3 => $"\r\n\x1b[36m● Read\x1b[0m(src/module{n + seed}/handler.cs)\r\n  ⎿  Read {n * 37 + seed} lines",
                4 => $"\r\n\x1b[33m● Bash\x1b[0m(dotnet test --filter Case{n + seed})\r\n  ⎿  Passed: {n * 3}, Failed: 0",
                5 or 6 => $"\r\n\x1b[32m✓\x1b[0m step {i} of the plan for seed {seed} finished without changes",
                7 => $"\r\n\x1b[31m✗\x1b[0m seed {seed}: assertion {n} failed on line {i * 13}",
                _ => $"\r\n  the run reported {n * seed * 3} findings across {n + 2} files and kept going",
            };
        }

        yield return "\r\n\r\n\x1b[35m>\x1b[0m ";
    }

    private static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double meanA = a.Average(), meanB = b.Average();
        double cov = 0, varA = 0, varB = 0;

        for (var i = 0; i < a.Count; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        return varA <= 0 || varB <= 0 ? 0 : cov / Math.Sqrt(varA * varB);
    }
}
