using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Which native Whisper runtime order gets tried for a given preference on a given host, and that the CPU is
/// always reachable underneath. The orders are asserted against what each NuGet package actually carries — the
/// planner used to be checked against the README instead, which is how Linux lost Vulkan and macOS was offered
/// CUDA. A wrong order here is invisible in production: the loader skips what it cannot find and transcribes
/// on the CPU without an error.
/// </summary>
public class WhisperBackendPlannerTests
{
    [Theory]
    [InlineData(WhisperHostPlatform.Windows)]
    [InlineData(WhisperHostPlatform.Linux)]
    public void BuildOrder_CpuPreference_IsCpuThenNoAvx(WhisperHostPlatform platform)
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Cpu, platform);

        order.Should().Equal(WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    [Theory]
    [InlineData(WhisperHostPlatform.Windows)]
    [InlineData(WhisperHostPlatform.Linux)]
    public void BuildOrder_CudaPreference_TriesBothCudaVariantsThenFallsBackToCpu(WhisperHostPlatform platform)
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Cuda, platform);

        order.Should().Equal(
            WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12,
            WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    /// <summary>
    /// Vulkan is published for Linux as well as Windows — one package carries both, verified against the real
    /// 1.9.1 nupkg. The planner claimed otherwise on the strength of issue #264, and every AMD-on-Linux host
    /// paid for it by never being offered its GPU.
    /// </summary>
    [Theory]
    [InlineData(WhisperHostPlatform.Windows)]
    [InlineData(WhisperHostPlatform.Linux)]
    public void BuildOrder_VulkanPreference_TriesVulkanThenCpu_OnBothPcPlatforms(WhisperHostPlatform platform)
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Vulkan, platform);

        order.Should().Equal(WhisperRuntimeBackend.Vulkan, WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    /// <summary>The AMD-on-Linux case that used to end on the CPU: Auto has to reach Vulkan there.</summary>
    [Theory]
    [InlineData(WhisperHostPlatform.Windows)]
    [InlineData(WhisperHostPlatform.Linux)]
    public void BuildOrder_AutoPreference_TriesCudaThenVulkanThenCpu(WhisperHostPlatform platform)
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Auto, platform);

        order.Should().Equal(
            WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, WhisperRuntimeBackend.Vulkan,
            WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    /// <summary>
    /// Nothing discrete is published for macOS — no CUDA, no Vulkan. Its GPU path is Metal, which is not a
    /// runtime family at all: it lives inside the CPU runtime. So the CPU entry is the whole honest order, and
    /// on Apple Silicon it is already the GPU one.
    /// </summary>
    [Theory]
    [InlineData(VoiceBackendPreference.Auto)]
    [InlineData(VoiceBackendPreference.Cuda)]
    [InlineData(VoiceBackendPreference.Vulkan)]
    [InlineData(VoiceBackendPreference.Cpu)]
    public void BuildOrder_OnMacOs_IsAlwaysJustCpu_WhateverWasAskedFor(VoiceBackendPreference preference)
    {
        var order = WhisperBackendPlanner.BuildOrder(preference, WhisperHostPlatform.MacOs);

        order.Should().Equal(WhisperRuntimeBackend.Cpu);
    }

    /// <summary>
    /// NoAvx publishes win-x64, win-x86 and linux-x64 natives and nothing for macOS, so offering it there
    /// would be a dead entry — a fallback that cannot be found is not a fallback.
    /// </summary>
    [Fact]
    public void BuildOrder_OnMacOs_NeverOffersNoAvx_BecauseItIsNotPublishedThere()
    {
        foreach (var preference in Enum.GetValues<VoiceBackendPreference>())
        {
            WhisperBackendPlanner.BuildOrder(preference, WhisperHostPlatform.MacOs)
                .Should().NotContain(WhisperRuntimeBackend.CpuNoAvx, $"preference={preference}");
        }
    }

    [Fact]
    public void BuildOrder_EveryOrder_EndsOnTheCpuSoTranscriptionAlwaysHasAFloor()
    {
        foreach (var preference in Enum.GetValues<VoiceBackendPreference>())
        {
            foreach (var platform in Enum.GetValues<WhisperHostPlatform>())
            {
                var order = WhisperBackendPlanner.BuildOrder(preference, platform);

                order.Should().Contain(WhisperRuntimeBackend.Cpu,
                    $"preference={preference}, platform={platform} must always have a CPU fallback");
                order[^1].Should().BeOneOf(WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
            }
        }
    }
}
