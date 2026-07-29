using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>The scrolling level history behind the voice overlay's live waveform (#34b).</summary>
public class WaveformLevelBufferTests
{
    [Fact]
    public void NewBuffer_StartsAllSilent()
    {
        var buffer = new WaveformLevelBuffer(4);

        Assert.Equal(4, buffer.BarCount);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, buffer.Levels);
    }

    [Fact]
    public void Push_PutsTheNewestLevelOnTheRight()
    {
        var buffer = new WaveformLevelBuffer(3);

        buffer.Push(0.5);

        Assert.Equal(new[] { 0.0, 0.0, 0.5 }, buffer.Levels);
    }

    [Fact]
    public void Push_ScrollsOlderLevelsLeft_AndDropsTheOldest()
    {
        var buffer = new WaveformLevelBuffer(3);

        buffer.Push(0.1);
        buffer.Push(0.2);
        buffer.Push(0.3);
        buffer.Push(0.4);

        Assert.Equal(new[] { 0.2, 0.3, 0.4 }, buffer.Levels);
    }

    [Fact]
    public void Push_ClampsOutOfRangeLevels()
    {
        var buffer = new WaveformLevelBuffer(2);

        buffer.Push(1.5);
        buffer.Push(-0.5);

        Assert.Equal(new[] { 1.0, 0.0 }, buffer.Levels);
    }

    [Fact]
    public void Reset_FlattensAllBars()
    {
        var buffer = new WaveformLevelBuffer(3);
        buffer.Push(0.7);
        buffer.Push(0.9);

        buffer.Reset();

        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, buffer.Levels);
    }

    [Fact]
    public void Constructor_NonPositiveBarCount_Throws()
    {
        var act = () => new WaveformLevelBuffer(0);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }
}
