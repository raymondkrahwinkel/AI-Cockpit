using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-68 slice 1: the host-aware transcription options. The point of the exercise is that a machine is never
/// offered a backend it cannot load — above all, no CUDA on a non-NVIDIA host — so these pin the mapping from
/// detected capabilities to the offered choices and the GPU picked by Auto.
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
}
