using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Backend-selection logic per environment: which native Whisper runtime order gets tried for a given
/// preference + OS, and that CPU is always reachable as the universal fallback (the AMD-on-Linux gap
/// this planner is explicit about — no published Linux Vulkan runtime, see WhisperBackendPlanner).
/// </summary>
public class WhisperBackendPlannerTests
{
    [Fact]
    public void BuildOrder_CpuPreference_IsCpuThenNoAvx_RegardlessOfOs()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Cpu, isWindows: true);

        order.Should().Equal(WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    [Fact]
    public void BuildOrder_CudaPreference_TriesBothCudaVariantsThenFallsBackToCpu()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Cuda, isWindows: false);

        order.Should().Equal(
            WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12,
            WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    [Fact]
    public void BuildOrder_VulkanPreference_OnWindows_TriesVulkanThenCpu()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Vulkan, isWindows: true);

        order.Should().Equal(WhisperRuntimeBackend.Vulkan, WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    /// <summary>
    /// No Vulkan runtime is published for Linux today: an explicit Vulkan preference on Linux (e.g. an
    /// AMD Linux desktop) has nothing to try and goes straight to the CPU fallback — never silently
    /// substitutes CUDA, which the operator did not ask for.
    /// </summary>
    [Fact]
    public void BuildOrder_VulkanPreference_OnLinux_FallsBackToCpuOnly()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Vulkan, isWindows: false);

        order.Should().Equal(WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    [Fact]
    public void BuildOrder_AutoPreference_OnWindows_TriesCudaThenVulkanThenCpu()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Auto, isWindows: true);

        order.Should().Equal(
            WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, WhisperRuntimeBackend.Vulkan,
            WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
    }

    /// <summary>
    /// The AMD-on-Linux scenario: Auto on Linux never offers Vulkan (nothing published for that OS),
    /// so a machine with no NVIDIA GPU ends up on the CPU tail — exactly the "hardware-agnostic,
    /// CPU-everywhere-as-baseline" requirement.
    /// </summary>
    [Fact]
    public void BuildOrder_AutoPreference_OnLinux_NeverOffersVulkan_AndEndsOnCpu()
    {
        var order = WhisperBackendPlanner.BuildOrder(VoiceBackendPreference.Auto, isWindows: false);

        order.Should().Equal(WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx);
        order.Should().NotContain(WhisperRuntimeBackend.Vulkan);
    }

    [Fact]
    public void BuildOrder_EveryOrder_EndsWithTheCpuTail()
    {
        foreach (var preference in Enum.GetValues<VoiceBackendPreference>())
        {
            foreach (var isWindows in new[] { true, false })
            {
                var order = WhisperBackendPlanner.BuildOrder(preference, isWindows);

                order.Should().EndWith([WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx],
                    $"preference={preference}, isWindows={isWindows} must always have a CPU fallback");
            }
        }
    }
}
