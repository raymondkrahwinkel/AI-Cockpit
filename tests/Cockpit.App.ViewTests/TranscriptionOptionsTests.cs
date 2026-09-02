using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-68 slice 1: the host-aware transcription options. The point of the exercise is that a machine is never
/// offered a backend it cannot load — above all, no CUDA on a non-NVIDIA host — so these pin the mapping from
/// detected capabilities to the offered choices, the hardware badge, and the per-selection advice.
/// </summary>
public class TranscriptionOptionsTests
{
    [Theory]
    // A CPU-only host is offered no GPU at all, and no host is ever offered a runtime it cannot load.
    [InlineData(false, false, new[] { VoiceBackendPreference.Auto, VoiceBackendPreference.Cpu })]
    [InlineData(false, true, new[] { VoiceBackendPreference.Auto, VoiceBackendPreference.Vulkan, VoiceBackendPreference.Cpu })]
    [InlineData(true, false, new[] { VoiceBackendPreference.Auto, VoiceBackendPreference.Cuda, VoiceBackendPreference.Cpu })]
    // There is one GPU entry, never two, and when both runtimes load it is the CUDA one.
    [InlineData(true, true, new[] { VoiceBackendPreference.Auto, VoiceBackendPreference.Cuda, VoiceBackendPreference.Cpu })]
    public void TheOfferedBackends_AreExactlyTheOnesThisHostCanLoad(
        bool cuda, bool vulkan, VoiceBackendPreference[] expected) =>
        Assert.Equal(
            expected,
            TranscriptionOptions.BackendChoices(new TranscriptionCapabilities(cuda, vulkan)).Select(choice => choice.Value));

    [Fact]
    public void TheGpuEntry_IsLabelledWithoutJargonTheOperatorHasToDecode()
    {
        var choices = TranscriptionOptions.BackendChoices(new TranscriptionCapabilities(CudaUsable: true, VulkanUsable: false));

        Assert.Equal("GPU (CUDA)", choices.Single(choice => choice.Value == VoiceBackendPreference.Cuda).Label);
    }

    [Theory]
    [InlineData(false, false, "No GPU acceleration detected — CPU only")]
    [InlineData(false, true, "Vulkan GPU available")]
    [InlineData(true, false, "NVIDIA CUDA GPU available")]
    public void TheHardwareBadge_NamesTheDetectedAcceleration(bool cuda, bool vulkan, string expected) =>
        Assert.Equal(expected, TranscriptionOptions.HardwareBadge(new TranscriptionCapabilities(cuda, vulkan)));

    [Theory]
    // Forcing the GPU is the one choice with a cost to warn about; CPU is the one that promises none.
    [InlineData(VoiceBackendPreference.Vulkan, false, true, "stutter")]
    [InlineData(VoiceBackendPreference.Cpu, false, false, "smooth")]
    // Auto says nothing of its own — it reports what the host turned out to have.
    [InlineData(VoiceBackendPreference.Auto, true, false, "GPU")]
    [InlineData(VoiceBackendPreference.Auto, false, false, "CPU")]
    public void TheAdvice_NamesWhatTheChoiceCostsOnThisHost(
        VoiceBackendPreference preference, bool cuda, bool vulkan, string expected) =>
        Assert.Contains(expected, TranscriptionOptions.Advice(preference, new TranscriptionCapabilities(cuda, vulkan)));
}
