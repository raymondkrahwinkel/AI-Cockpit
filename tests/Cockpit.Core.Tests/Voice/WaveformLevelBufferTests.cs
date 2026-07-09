using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>The scrolling level history behind the voice overlay's live waveform (#34b).</summary>
public class WaveformLevelBufferTests
{
    [Fact]
    public void NewBuffer_StartsAllSilent()
    {
        var buffer = new WaveformLevelBuffer(4);

        buffer.BarCount.Should().Be(4);
        buffer.Levels.Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void Push_PutsTheNewestLevelOnTheRight()
    {
        var buffer = new WaveformLevelBuffer(3);

        buffer.Push(0.5);

        buffer.Levels.Should().Equal(0, 0, 0.5);
    }

    [Fact]
    public void Push_ScrollsOlderLevelsLeft_AndDropsTheOldest()
    {
        var buffer = new WaveformLevelBuffer(3);

        buffer.Push(0.1);
        buffer.Push(0.2);
        buffer.Push(0.3);
        buffer.Push(0.4);

        buffer.Levels.Should().Equal(0.2, 0.3, 0.4);
    }

    [Fact]
    public void Push_ClampsOutOfRangeLevels()
    {
        var buffer = new WaveformLevelBuffer(2);

        buffer.Push(1.5);
        buffer.Push(-0.5);

        buffer.Levels.Should().Equal(1, 0);
    }

    [Fact]
    public void Reset_FlattensAllBars()
    {
        var buffer = new WaveformLevelBuffer(3);
        buffer.Push(0.7);
        buffer.Push(0.9);

        buffer.Reset();

        buffer.Levels.Should().Equal(0, 0, 0);
    }

    [Fact]
    public void Constructor_NonPositiveBarCount_Throws()
    {
        var act = () => new WaveformLevelBuffer(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
