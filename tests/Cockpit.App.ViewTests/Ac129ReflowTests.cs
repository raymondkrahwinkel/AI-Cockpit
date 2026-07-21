using System.Text;
using Exclr8.Terminal.Buffer;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-129: an `ls -l`-style line wider than the pane soft-wraps, and the operator saw it come out mangled —
/// broken mid-word on the wrong columns. The leading hypothesis was Exclr8's reflow-on-resize. These drive the
/// vendored <see cref="TerminalBuffer"/> exactly as TerminalControl does — feed a long line, resize the column
/// count each way, and assert the visible text survives verbatim.
/// <para>
/// They pass on the stock 1.0.7 reflow: the buffer re-wraps the line without dropping or shifting a character in
/// any of the four resize directions. That clears the buffer-reflow of the mangling and points AC-129's on-screen
/// corruption at the render/redraw path (stale cells, or a transient pty-vs-render width mismatch during the
/// debounced resize), which a headless buffer read cannot see — confirming it needs a visual repro. Kept as a
/// regression guard so a future renderer patch can't quietly break the reflow the operator does depend on.
/// </para>
/// </summary>
public class Ac129ReflowTests
{
    private const string Line =
        "drwxr-xr-x  1 raymond staff  4096 Jul 21 10:00 wit_mnd_nl_std_volumes_report_final.txt";

    [Theory]
    [InlineData(20, 40)] // widen
    [InlineData(20, 15)] // narrow
    [InlineData(30, 80)] // widen far
    [InlineData(80, 24)] // narrow far
    public void LongLine_SurvivesResize_WithTextIntact(int startCols, int newCols)
    {
        var buffer = new TerminalBuffer(startCols, 8) { ScrollbackLimit = 5000 };
        buffer.Write(Encoding.UTF8.GetBytes(Line));

        _VisibleText(buffer).Should().Be(Line, "the soft-wrapped line reads back verbatim before any resize");

        buffer.Resize(newCols, 8);

        _VisibleText(buffer).Should().Be(Line, "reflow must re-wrap the line without dropping or shifting characters");
    }

    /// <summary>The whole visible screen as one string, continuation halves and trailing blanks dropped —
    /// so a soft-wrapped line reads back as the single logical line it is.</summary>
    private static string _VisibleText(TerminalBuffer buffer)
    {
        var text = new StringBuilder();
        for (var row = 0; row < buffer.Rows; row++)
        {
            var cells = buffer.GetVisibleRow(row);
            for (var col = 0; col < buffer.Cols; col++)
            {
                if ((cells[col].Flags2 & CellFlags2.IsContinuation) != 0)
                {
                    continue;
                }

                var rune = cells[col].Rune;
                text.Append(rune == 0 ? ' ' : char.ConvertFromUtf32(rune));
            }
        }

        return text.ToString().TrimEnd(' ');
    }
}
