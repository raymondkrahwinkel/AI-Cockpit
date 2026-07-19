using System.Text;
using Avalonia.Controls;
using Exclr8.Terminal;
using Exclr8.Terminal.Buffer;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-70 / AC-66 diagnosis, and a regression guard for a real TtyView contract. The pty pump accumulates raw
/// bytes and <c>_FlushOutput</c> hands Exclr8 whatever has piled up each ~33 ms tick, so a flush boundary can fall
/// in the middle of a multi-byte UTF-8 sequence. These pin that this is safe: Exclr8 keeps a streaming decoder
/// across <c>Write</c> calls, so a character split across two writes is reassembled, and a whole line of
/// —/→/✅ survives intact in the cell buffer.
/// <para>
/// They also record the AC-66 finding: the em-dash/arrow/emoji mangling the operator saw while scrolling is NOT a
/// byte-level decode or flush-split fault — both cases below land correct cells. That rules out the leading
/// hypothesis and points AC-66's root cause at the render/reflow layer (Exclr8 re-shapes visible text every frame,
/// no buffer involvement — see the AC-57 note in TtyView) or wide-character display-width accounting during a
/// scroll/redraw, neither of which shows up in a headless buffer read; confirming the on-screen corruption needs a
/// visual repro in the running app.
/// </para>
/// </summary>
[Collection("avalonia")]
public class TtyMultiByteRenderTests
{
    private const string Line = "0.22.0→0.22.1 gemerged + store — done ✅ toggles";

    [Fact]
    public void WholeLineInOneWrite_KeepsEveryMultiByteCharacter() => HeadlessAvalonia.Run(() =>
    {
        var terminal = _NewTerminal();

        terminal.Write(Encoding.UTF8.GetBytes(Line + "\r\n"));

        var screen = _ReadScreen(terminal);
        screen.Should().Contain("0.22.0→0.22.1");
        screen.Should().Contain("store — done ✅");
        screen.Should().Contain("toggles");
    });

    [Fact]
    public void MultiByteCharSplitAcrossTwoWrites_IsStillReassembled() => HeadlessAvalonia.Run(() =>
    {
        var terminal = _NewTerminal();
        var bytes = Encoding.UTF8.GetBytes(Line + "\r\n");

        // Split inside the em-dash (— = E2 80 94, 3 bytes): find its first byte and cut one byte into it, so the
        // first write ends mid-character — exactly what a pty-output flush boundary can do.
        var dashStart = _IndexOfFirstByte(bytes, 0xE2);
        var split = dashStart + 1;

        terminal.Write(bytes[..split]);
        terminal.Write(bytes[split..]);

        var screen = _ReadScreen(terminal);
        screen.Should().Contain("0.22.0→0.22.1");
        screen.Should().Contain("store — done ✅");
        screen.Should().Contain("toggles");
    });

    private static TerminalControl _NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Content = terminal, Width = 1400, Height = 400 };
        window.Show();
        window.UpdateLayout();
        return terminal;
    }

    private static int _IndexOfFirstByte(byte[] bytes, byte value)
    {
        var index = Array.IndexOf(bytes, value);
        index.Should().BeGreaterThan(0, "the multi-byte character has to be present to split it");
        return index;
    }

    private static string _ReadScreen(TerminalControl terminal)
    {
        var buffer = terminal.Buffer;
        var builder = new StringBuilder();
        for (var row = 0; row < buffer.Rows; row++)
        {
            var cells = buffer.GetRowForRender(row);
            if (cells is null)
            {
                continue;
            }

            builder.Append(RowText.Build(cells, out _)).Append('\n');
        }

        return builder.ToString();
    }
}
