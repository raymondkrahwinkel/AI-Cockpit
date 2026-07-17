using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// AC-68 slice 3: the calibration verdict. A measurement is only useful if it can overturn the rule table — a GPU
/// that measured a real desktop hitch should be moved off, whatever slice 2 guessed — so these pin the smooth/hitch
/// threshold, the "suggest CPU instead" rule, and the rationale wording.
/// </summary>
public class TranscriptionCalibrationReportTests
{
    private static TranscriptionCalibration Measured(double hitchMs, VoiceBackendPreference backend) =>
        new(LatencyMs: 800, HitchMs: hitchMs, Backend: backend, Model: "large-v3-turbo");

    [Theory]
    [InlineData(3, true)]
    [InlineData(16, true)]
    [InlineData(41, false)]
    public void IsDesktopSmooth_TurnsOnTheOneFrameThreshold(double hitchMs, bool expected) =>
        TranscriptionCalibrationReport.IsDesktopSmooth(Measured(hitchMs, VoiceBackendPreference.Cpu)).Should().Be(expected);

    [Fact]
    public void AGpuThatHitched_IsFlaggedToMoveToTheCpu()
    {
        TranscriptionCalibrationReport.SuggestsCpuInstead(Measured(41, VoiceBackendPreference.Vulkan)).Should().BeTrue();
        TranscriptionCalibrationReport.Rationale(Measured(41, VoiceBackendPreference.Vulkan)).Should().Contain("CPU");
    }

    [Fact]
    public void AGpuThatStayedSmooth_IsNotFlagged()
    {
        TranscriptionCalibrationReport.SuggestsCpuInstead(Measured(3, VoiceBackendPreference.Cuda)).Should().BeFalse();
    }

    [Fact]
    public void TheCpu_IsNeverFlaggedToMoveToTheCpu_EvenIfItHitched()
    {
        // A hitch on the CPU is not an argument to switch to the CPU — there is nowhere smoother to send it.
        TranscriptionCalibrationReport.SuggestsCpuInstead(Measured(80, VoiceBackendPreference.Cpu)).Should().BeFalse();
    }

    [Fact]
    public void Rationale_ForASmoothRun_ReadsAsSmooth() =>
        TranscriptionCalibrationReport.Rationale(Measured(3, VoiceBackendPreference.Cpu)).Should().Contain("smooth");
}
