using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Pure presentation helpers for the Options → Voice → Transcribe page (AC-68): they turn detected
/// <see cref="TranscriptionCapabilities"/> into the host-aware backend list, the hardware badge, and the
/// one-line advice for a chosen backend. Kept out of the view model so the host-awareness — never offering
/// CUDA where it cannot load — is unit-testable without an Avalonia platform.
/// </summary>
public static class TranscriptionOptions
{
    /// <summary>
    /// The backend choices to offer on this host: always Auto and CPU, plus a single jargon-free "GPU" entry
    /// only when a GPU runtime actually loads here — CUDA preferred over Vulkan when both are present. A
    /// CPU-only host is never shown a GPU option, so a non-NVIDIA machine cannot be handed CUDA.
    /// </summary>
    public static IReadOnlyList<VoiceBackendPreferenceOption> BackendChoices(TranscriptionCapabilities capabilities)
    {
        var choices = new List<VoiceBackendPreferenceOption>
        {
            new("Auto (recommended)", VoiceBackendPreference.Auto),
        };

        if (capabilities.CudaUsable)
        {
            choices.Add(new("GPU (CUDA)", VoiceBackendPreference.Cuda));
        }
        else if (capabilities.VulkanUsable)
        {
            choices.Add(new("GPU (Vulkan)", VoiceBackendPreference.Vulkan));
        }

        choices.Add(new("CPU", VoiceBackendPreference.Cpu));
        return choices;
    }

    /// <summary>A short badge describing the detected acceleration, so the choices read as host-aware.</summary>
    public static string HardwareBadge(TranscriptionCapabilities capabilities) => capabilities switch
    {
        { CudaUsable: true } => "NVIDIA CUDA GPU available",
        { VulkanUsable: true } => "Vulkan GPU available",
        _ => "No GPU acceleration detected — CPU only",
    };

    /// <summary>One line explaining what the chosen backend does on this machine.</summary>
    public static string Advice(VoiceBackendPreference selection, TranscriptionCapabilities capabilities) => selection switch
    {
        VoiceBackendPreference.Cpu =>
            "Runs on the CPU — keeps the desktop smooth; a large model is a little slower per sentence.",
        VoiceBackendPreference.Cuda or VoiceBackendPreference.Vulkan =>
            "Forces the GPU. Faster to transcribe, but a single GPU that also draws your screen can make the desktop stutter — switch to CPU if you see it.",
        _ when capabilities.GpuUsable =>
            "Auto picks the fastest backend this machine can load — the GPU when it is available.",
        _ =>
            "Auto runs on the CPU: no GPU acceleration was detected on this machine.",
    };
}
