using System.Text;
using FluentAssertions;

namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>
/// <see cref="ProcessCliSubprocess"/>'s capped line reader (P1-9a) — kimi acp's stdout/stderr is untrusted, and
/// the previous implementation (<c>StreamReader.ReadLineAsync</c>) had no length limit of its own: a child that
/// never emits a newline would grow the accumulating buffer until the host process itself ran out of memory.
/// Proves an oversized line is dropped rather than buffered without bound, and that the reader re-synchronises
/// on the very next NDJSON line afterwards instead of desyncing or dying.
/// </summary>
public class ProcessCliSubprocessTests
{
    [Fact]
    public async Task ReadLinesAsync_ALineOverTheCap_IsDropped_AndTheNextNormalLineStillArrives()
    {
        var subprocess = new ProcessCliSubprocess();
        var oversizedLine = new string('a', ProcessCliSubprocess.MaxLineLengthChars + 10);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{oversizedLine}\nnormal-line\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        await foreach (var line in subprocess.ReadLinesAsyncForTests(reader))
        {
            lines.Add(line);
        }

        lines.Should().ContainSingle().Which.Should().Be("normal-line", "the oversized line must be dropped rather than returned in full, and the pump must keep living for the next line");
        subprocess.DroppedOversizedLineCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadLinesAsync_UnderTheCap_ReturnsEveryLineUnchanged_AndDropsNothing()
    {
        var subprocess = new ProcessCliSubprocess();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\nthree\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        await foreach (var line in subprocess.ReadLinesAsyncForTests(reader))
        {
            lines.Add(line);
        }

        lines.Should().Equal("one", "two", "three");
        subprocess.DroppedOversizedLineCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadLinesAsync_ALineThatNeverEndsInANewline_AndIsOverTheCap_IsCountedAsDropped()
    {
        var subprocess = new ProcessCliSubprocess();
        var oversizedLine = new string('b', ProcessCliSubprocess.MaxLineLengthChars + 10);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(oversizedLine));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        await foreach (var line in subprocess.ReadLinesAsyncForTests(reader))
        {
            lines.Add(line);
        }

        lines.Should().BeEmpty("a never-terminated oversized line must never be yielded, even at end of stream");
        subprocess.DroppedOversizedLineCount.Should().Be(1);
    }
}
