using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// AC-68 slice 2: the hardware-aware rule table. The governing insight is that a single GPU which also draws the
/// screen should transcribe on the CPU so a long dictation does not stutter the desktop — these pin that and the
/// other rows (NVIDIA fast path, second-GPU acceleration, Apple/Metal, no-GPU fallback).
/// </summary>
public class TranscriptionRecommenderTests
{
    private static readonly TranscriptionCapabilities CudaCaps = new(CudaUsable: true, VulkanUsable: false);
    private static readonly TranscriptionCapabilities VulkanCaps = new(CudaUsable: false, VulkanUsable: true);

    [Fact]
    public void AnAmdGpuThatDrivesTheDisplay_IsSteeredToTheCpu_ToKeepTheDesktopSmooth()
    {
        var gpu = new GpuHardware(GpuVendor.Amd, "AMD Radeon RX 6700 XT", DrivesDisplay: true, VideoMemoryBytes: 12L * 1024 * 1024 * 1024);

        var recommendation = TranscriptionRecommender.Recommend(VulkanCaps, gpu, WhisperHostPlatform.Windows);

        recommendation.Backend.Should().Be(VoiceBackendPreference.Cpu);
        recommendation.Model.Should().Be("large-v3-turbo");
        recommendation.Reason.Should().ContainAll("screen", "stutter");
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
    public void ASmallNvidiaThatDrivesTheDisplay_PrefersTheCpu_OverAStarvedFastPath()
    {
        // Below the VRAM bar and it is the display adapter: keep the desktop smooth rather than force the GPU.
        var gpu = new GpuHardware(GpuVendor.Nvidia, "NVIDIA GeForce MX150", DrivesDisplay: true, VideoMemoryBytes: 2L * 1024 * 1024 * 1024);

        TranscriptionRecommender.Recommend(CudaCaps, gpu, WhisperHostPlatform.Windows)
            .Backend.Should().Be(VoiceBackendPreference.Cpu);
    }
}
