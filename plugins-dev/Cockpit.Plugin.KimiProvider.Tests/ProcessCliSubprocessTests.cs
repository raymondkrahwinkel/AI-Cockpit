using System.Text;

namespace Cockpit.Plugin.KimiProvider.Tests;

// `ProcessCliSubprocess`'s capped line reader (P1-9a) — kimi acp's stdout/stderr is untrusted, and
// the previous implementation (`StreamReader.ReadLineAsync`) had no length limit of its own: a child that
// never emits a newline would grow the accumulating buffer until the host process itself ran out of memory.
// Proves an oversized line is dropped rather than buffered without bound, and that the reader re-synchronises
// on the very next NDJSON line afterwards instead of desyncing or dying.
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

        Assert.Equal("normal-line", Assert.Single(lines));
        Assert.Equal(1, subprocess.DroppedOversizedLineCount);
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

        Assert.Equal(new[] { "one", "two", "three" }, lines);
        Assert.Equal(0, subprocess.DroppedOversizedLineCount);
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

        Assert.Empty(lines);
        Assert.Equal(1, subprocess.DroppedOversizedLineCount);
    }
}
