using Cockpit.Infrastructure.Voice;

namespace Cockpit.Core.Tests.Voice;

// Covers AC-708: the saved `TtsSpeed` reaches the generation config through `ClampSpeed`, and out-of-range
// values (including 0/negative) never pass through unclamped. `SynthesizeAsync` itself needs the native
// SupertonicTTS model, so the clamp is exercised directly rather than through the full synthesis path.
public class SherpaOnnxTextToSpeechServiceTests
{
    [Theory]
    [InlineData(1.0, 1.0f)]
    [InlineData(0.5, 0.5f)]
    [InlineData(2.0, 2.0f)]
    [InlineData(1.35, 1.35f)]
    public void ClampSpeed_WithinRange_PassesThroughUnchanged(double speed, float expected) =>
        Assert.Equal(expected, SherpaOnnxTextToSpeechService.ClampSpeed(speed));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.1)]
    public void ClampSpeed_ZeroOrBelowMinimum_ClampsToMinimum(double speed) =>
        Assert.Equal(0.5f, SherpaOnnxTextToSpeechService.ClampSpeed(speed));

    [Fact]
    public void ClampSpeed_AboveMaximum_ClampsToMaximum() =>
        Assert.Equal(2.0f, SherpaOnnxTextToSpeechService.ClampSpeed(5.0));
}
