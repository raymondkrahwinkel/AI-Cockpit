using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// AC-68: the calibration verdict. The rule is CPU preference decided on measurements — the CPU keeps the desktop
/// perfectly smooth, so it wins unless the GPU is meaningfully faster, and the margin widens when the GPU hitches.
/// These pin the thresholds, the "prefer CPU / prefer GPU" boundary, and the rationale wording.
/// </summary>
public class TranscriptionCalibrationReportTests
{
    private static BackendMeasurement Cpu(double latencyMs, double hitchMs = 0) =>
        new(VoiceBackendPreference.Cpu, latencyMs, hitchMs);

    private static BackendMeasurement Gpu(double latencyMs, double hitchMs) =>
        new(VoiceBackendPreference.Vulkan, latencyMs, hitchMs);

    [Theory]
    [InlineData(3, true)]
    [InlineData(16, true)]
    [InlineData(41, false)]
    public void IsSmooth_TurnsOnTheOneFrameThreshold(double hitchMs, bool expected) =>
        TranscriptionCalibrationReport.IsSmooth(Cpu(800, hitchMs)).Should().Be(expected);

    [Fact]
    public void CpuIsPreferred_WhenItIsCloseToTheGpuAndTheGpuIsSmooth()
    {
        // CPU only ~25% slower than a smooth GPU: keep the desktop perfectly smooth for a negligible speed cost.
        var (backend, rationale) = TranscriptionCalibrationReport.Decide([Cpu(2500), Gpu(2000, hitchMs: 4)]);

        backend.Should().Be(VoiceBackendPreference.Cpu);
        rationale.Should().Contain("smooth");
    }

    [Fact]
    public void GpuIsChosen_WhenTheCpuIsMuchSlower()
    {
        // The pathological AMD case that started this: 35s on the CPU is unusable next to a 3s GPU.
        var (backend, rationale) = TranscriptionCalibrationReport.Decide([Cpu(35000), Gpu(3000, hitchMs: 6)]);

        backend.Should().Be(VoiceBackendPreference.Vulkan);
        rationale.Should().Contain("faster");
    }

    [Fact]
    public void AHitchingGpu_WidensTheCpuPreference()
    {
        // CPU is 2.5x slower than the GPU, but the GPU hitches the desktop: the wider margin (3x) keeps the CPU.
        TranscriptionCalibrationReport.Decide([Cpu(5000), Gpu(2000, hitchMs: 40)])
            .Backend.Should().Be(VoiceBackendPreference.Cpu);

        // The same speed gap with a smooth GPU falls outside the tighter margin (1.5x): the GPU wins.
        TranscriptionCalibrationReport.Decide([Cpu(5000), Gpu(2000, hitchMs: 4)])
            .Backend.Should().Be(VoiceBackendPreference.Vulkan);
    }

    [Fact]
    public void WithOnlyTheCpuMeasured_ItIsChosen()
    {
        TranscriptionCalibrationReport.Decide([Cpu(4200)]).Backend.Should().Be(VoiceBackendPreference.Cpu);
    }

    [Fact]
    public void RationaleForACpuChoice_NamesBothTimes()
    {
        TranscriptionCalibrationReport.Decide([Cpu(2200), Gpu(2000, hitchMs: 3)])
            .Rationale.Should().ContainAll("CPU", "GPU");
    }

    [Fact]
    public void RecommendModel_PicksTheMostAccurateModelThatStaysResponsive()
    {
        // On a fast backend every model is well within budget, so the most accurate wins.
        var ladder = new[]
        {
            new ModelMeasurement("large-v3-turbo", 500),
            new ModelMeasurement("small", 200),
            new ModelMeasurement("base", 120),
            new ModelMeasurement("tiny", 60),
        };

        TranscriptionCalibrationReport.RecommendModel(ladder).Model.Should().Be("large-v3-turbo");
    }

    [Fact]
    public void RecommendModel_DropsToASmallerModel_WhenTheAccurateOnesAreTooSlow()
    {
        // A slow CPU-only box: only the smaller models come in under the responsiveness budget.
        var ladder = new[]
        {
            new ModelMeasurement("large-v3-turbo", 35000),
            new ModelMeasurement("small", 8000),
            new ModelMeasurement("base", 2800),
            new ModelMeasurement("tiny", 1500),
        };

        // base (2.8s) is the most accurate model still under the 3s budget; turbo/small are too slow.
        TranscriptionCalibrationReport.RecommendModel(ladder).Model.Should().Be("base");
    }

    [Fact]
    public void RecommendModel_FallsBackToTheFastest_WhenNothingIsResponsive()
    {
        var ladder = new[]
        {
            new ModelMeasurement("large-v3-turbo", 35000),
            new ModelMeasurement("small", 12000),
            new ModelMeasurement("tiny", 6000),
        };

        TranscriptionCalibrationReport.RecommendModel(ladder).Model.Should().Be("tiny");
    }

    [Fact]
    public void RecommendModel_NeverPrefersAnUnknownModelOverAMeasuredKnownOne()
    {
        // A custom/quantized name ranks below every curated model, so it cannot be recommended just for being fast.
        var ladder = new[]
        {
            new ModelMeasurement("large-v3-turbo", 500),
            new ModelMeasurement("my-custom-q5", 100),
        };

        TranscriptionCalibrationReport.RecommendModel(ladder).Model.Should().Be("large-v3-turbo");
    }
}
