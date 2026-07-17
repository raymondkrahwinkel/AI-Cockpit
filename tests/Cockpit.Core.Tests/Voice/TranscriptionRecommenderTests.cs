using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// AC-68: the hardware-aware rule table — now only the <em>first-run guess</em> before a calibration exists, so it
/// leans to the fast path: any usable GPU is used, because the CPU alternative can be unusably slow, and the
/// measured calibration is what moves Auto to the CPU if the GPU really stutters the desktop. These pin that plus
/// the NVIDIA fast-path reason, Apple/Metal, and the no-GPU fallback.
/// </summary>
public class TranscriptionRecommenderTests
{
    private static readonly TranscriptionCapabilities CudaCaps = new(CudaUsable: true, VulkanUsable: false);
    private static readonly TranscriptionCapabilities VulkanCaps = new(CudaUsable: false, VulkanUsable: true);

    [Fact]
    public void AnAmdGpuThatDrivesTheDisplay_NowUsesTheGpu_AndPointsToCalibration()
    {
        // The regression this whole change fixes: steering a capable Vulkan GPU to the CPU made a sentence take
        // tens of seconds. The guess now uses the GPU and asks the operator to calibrate to confirm smoothness.
        var gpu = new GpuHardware(GpuVendor.Amd, "AMD Radeon RX 6700 XT", DrivesDisplay: true, VideoMemoryBytes: 12L * 1024 * 1024 * 1024);

        var recommendation = TranscriptionRecommender.Recommend(VulkanCaps, gpu, WhisperHostPlatform.Windows);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Vulkan);
        recommendation.Model.Should().Be("large-v3-turbo");
        recommendation.Reason.Should().ContainAll("screen", "calibration");
        recommendation.Badges.Should().Contain("drives display").And.Contain("no CUDA");
    }

    [Fact]
    public void AnNvidiaGpuWithCudaAndEnoughVram_TakesTheFastPath()
    {
        var gpu = new GpuHardware(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", DrivesDisplay: true, VideoMemoryBytes: 12L * 1024 * 1024 * 1024);

        var recommendation = TranscriptionRecommender.Recommend(CudaCaps, gpu, WhisperHostPlatform.Windows);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Cuda);
        recommendation.Model.Should().Be("large-v3-turbo");
        recommendation.Badges.Should().Contain("CUDA");
    }

    [Fact]
    public void AUsableGpuThatDoesNotDriveTheDisplay_IsAllowedToAccelerate()
    {
        // A second card / headless box: nothing to stutter, so let it transcribe on the GPU.
        var gpu = new GpuHardware(GpuVendor.Amd, "AMD Instinct", DrivesDisplay: false, VideoMemoryBytes: 0);

        var recommendation = TranscriptionRecommender.Recommend(VulkanCaps, gpu, WhisperHostPlatform.Windows);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Vulkan);
    }

    [Fact]
    public void AppleSilicon_RunsThroughMetalInTheCpuRuntime()
    {
        var recommendation = TranscriptionRecommender.Recommend(
            TranscriptionCapabilities.CpuOnly,
            new GpuHardware(GpuVendor.Apple, "Apple Silicon", DrivesDisplay: true, VideoMemoryBytes: 0),
            WhisperHostPlatform.MacOs);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Cpu);
        recommendation.Reason.Should().Contain("Metal");
        recommendation.Badges.Should().Contain("Metal");
    }

    [Fact]
    public void NoGpuAtAll_FallsBackToALighterModelOnTheCpu()
    {
        var recommendation = TranscriptionRecommender.Recommend(
            TranscriptionCapabilities.CpuOnly, GpuHardware.None, WhisperHostPlatform.Windows);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Cpu);
        recommendation.Model.Should().Be("small");
    }

    [Fact]
    public void ASmallNvidiaBelowTheVramBar_StillGuessesTheGpu_AndLetsCalibrationCorrect()
    {
        // Below the fast-path VRAM bar, so it does not get the "fastest path" wording — but a usable CUDA device
        // is still the guess (the runtime falls to the CPU tail if it cannot load, and calibration measures reality).
        var gpu = new GpuHardware(GpuVendor.Nvidia, "NVIDIA GeForce MX150", DrivesDisplay: true, VideoMemoryBytes: 2L * 1024 * 1024 * 1024);

        TranscriptionRecommender.Recommend(CudaCaps, gpu, WhisperHostPlatform.Windows)
            .Backend.Should().Be(VoiceBackendPreference.Cuda);
    }
}
