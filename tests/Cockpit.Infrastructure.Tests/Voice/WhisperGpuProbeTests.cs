using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Voice;

/// <summary>
/// The probe answers about the hardware it is running on, so most of it can only be asserted against a machine
/// with that hardware. What is true everywhere is pinned here; the CUDA and Vulkan branches are proven live
/// (see docs/binary-size-and-on-demand-runtimes.md), not in a unit test that would only assert this box.
/// </summary>
public class WhisperGpuProbeTests
{
    /// <summary>
    /// The CPU runtimes ship with the app. Calling them "usable" would send the cache looking for a package
    /// that does not exist, and worse, stop the walk before it reached the GPU backend that does.
    /// </summary>
    [Theory]
    [InlineData(WhisperRuntimeBackend.Cpu)]
    [InlineData(WhisperRuntimeBackend.CpuNoAvx)]
    public void IsUsable_CpuBackends_AreNeverSomethingToFetch(WhisperRuntimeBackend backend)
    {
        WhisperGpuProbe.IsUsable(backend).Should().BeFalse();
    }

    /// <summary>
    /// Whatever this machine has, asking must not throw: the probe loads a native cudart it does not own, and
    /// a fault there may not take dictation down — the CPU runtime is always there to fall back to.
    /// </summary>
    [Theory]
    [InlineData(WhisperRuntimeBackend.Cuda)]
    [InlineData(WhisperRuntimeBackend.Cuda12)]
    [InlineData(WhisperRuntimeBackend.Vulkan)]
    public void IsUsable_GpuBackends_AnswerInsteadOfThrowingOnAnyHardware(WhisperRuntimeBackend backend)
    {
        var probing = () => WhisperGpuProbe.IsUsable(backend);

        probing.Should().NotThrow();
    }

    /// <summary>
    /// Cuda and Cuda12 are separate backends because they want different CUDA majors (13 and 12). A probe that
    /// said yes to both on one host would defeat the point of the split — the mismatch it exists to catch falls
    /// silently back to the CPU.
    /// </summary>
    [Fact]
    public void IsUsable_TheTwoCudaBackends_AreNotBothUsableOnOneHost()
    {
        var cuda = WhisperGpuProbe.IsUsable(WhisperRuntimeBackend.Cuda);
        var cuda12 = WhisperGpuProbe.IsUsable(WhisperRuntimeBackend.Cuda12);

        (cuda && cuda12).Should().BeFalse();
    }
}
