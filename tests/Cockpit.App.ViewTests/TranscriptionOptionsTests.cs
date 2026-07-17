using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-68 slice 1: the host-aware transcription options. The point of the exercise is that a machine is never
/// offered a backend it cannot load — above all, no CUDA on a non-NVIDIA host — so these pin the mapping from
/// detected capabilities to the offered choices, the hardware badge, and the per-selection advice.
/// </summary>
public class TranscriptionOptionsTests
{
    private static readonly TranscriptionCapabilities CpuOnly = TranscriptionCapabilities.CpuOnly;
    private static readonly TranscriptionCapabilities Cuda = new(CudaUsable: true, VulkanUsable: false);
    private static readonly TranscriptionCapabilities Vulkan = new(CudaUsable: false, VulkanUsable: true);
    private static readonly TranscriptionCapabilities Both = new(CudaUsable: true, VulkanUsable: true);

    [Fact]
    public void ACpuOnlyHost_IsOfferedAutoAndCpuOnly_NeverAGpu()
    {
        TranscriptionOptions.BackendChoices(CpuOnly).Select(choice => choice.Value)
            .Should().Equal(VoiceBackendPreference.Auto, VoiceBackendPreference.Cpu);
    }

    [Fact]
    public void AVulkanHost_IsOfferedAGpuOption_ButNeverCuda()
    {
        var values = TranscriptionOptions.BackendChoices(Vulkan).Select(choice => choice.Value).ToList();
        values.Should().Equal(VoiceBackendPreference.Auto, VoiceBackendPreference.Vulkan, VoiceBackendPreference.Cpu);
        values.Should().NotContain(VoiceBackendPreference.Cuda, "CUDA must never be offered where it cannot load");
    }

    [Fact]
    public void AnNvidiaHost_IsOfferedCuda_AsAPlainGpuLabel()
    {
        var gpu = TranscriptionOptions.BackendChoices(Cuda).Single(choice => choice.Value == VoiceBackendPreference.Cuda);
        gpu.Label.Should().Be("GPU (CUDA)");
    }

    [Fact]
    public void WhenBothLoad_TheSingleGpuEntry_PrefersCuda()
    {
        var choices = TranscriptionOptions.BackendChoices(Both);
        choices.Should().ContainSingle(choice =>
            choice.Value == VoiceBackendPreference.Cuda || choice.Value == VoiceBackendPreference.Vulkan);
        choices.Should().Contain(choice => choice.Value == VoiceBackendPreference.Cuda);
    }

    [Theory]
    [InlineData(false, false, "No GPU acceleration detected — CPU only")]
    [InlineData(false, true, "Vulkan GPU available")]
    [InlineData(true, false, "NVIDIA CUDA GPU available")]
    public void TheHardwareBadge_NamesTheDetectedAcceleration(bool cuda, bool vulkan, string expected) =>
        TranscriptionOptions.HardwareBadge(new TranscriptionCapabilities(cuda, vulkan)).Should().Be(expected);

    [Fact]
    public void Advice_ForcingTheGpu_WarnsAboutDesktopStutter() =>
        TranscriptionOptions.Advice(VoiceBackendPreference.Vulkan, Vulkan).Should().Contain("stutter");

    [Fact]
    public void Advice_Cpu_PromisesASmoothDesktop() =>
        TranscriptionOptions.Advice(VoiceBackendPreference.Cpu, CpuOnly).Should().Contain("smooth");

    [Fact]
    public void Advice_Auto_ReflectsWhetherAGpuWasDetected()
    {
        TranscriptionOptions.Advice(VoiceBackendPreference.Auto, Cuda).Should().Contain("GPU");
        TranscriptionOptions.Advice(VoiceBackendPreference.Auto, CpuOnly).Should().Contain("CPU");
    }
}
