namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutBriefTests
{
    [Fact]
    public void Compose_WithAnAngle_NamesItAsTheAngleRatherThanRunningItOnAfterTheTask()
    {
        var brief = FanOutBrief.Compose("Speed up the importer.", "the smallest change that works");

        Assert.StartsWith("Speed up the importer.", brief, StringComparison.Ordinal);
        Assert.Contains("Take this angle: the smallest change that works", brief, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_WithoutAnAngle_IsTheTaskAlone(string angle)
    {
        Assert.Equal("Speed up the importer.", FanOutBrief.Compose("  Speed up the importer.  ", angle));
    }

    [Fact]
    public void Label_AMultiLineTask_TakesTheFirstLine()
    {
        Assert.Equal("Speed up the importer", FanOutBrief.Label("Speed up the importer\n\nIt takes 40s on a cold cache."));
    }

    [Fact]
    public void Label_ALongFirstLine_IsCutAndMarked()
    {
        var label = FanOutBrief.Label(new string('a', 200));

        Assert.Equal(61, label.Length);
        Assert.EndsWith("…", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Label_NothingTyped_IsEmptyRatherThanThrowing()
    {
        Assert.Equal(string.Empty, FanOutBrief.Label("   "));
    }
}
