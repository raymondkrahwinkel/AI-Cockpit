using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

// AC-68 presentation helpers turn capabilities into host-safe choices, badges and advice without Avalonia.
public static class TranscriptionOptions
{
    // The backend choices to offer on this host: always Auto and CPU, plus a jargon-free "GPU" entry only when
    // a GPU runtime actually loads here — CUDA preferred over Vulkan. AC-1881: with several selectable GPUs, one
    // "GPU <name>" entry each, and Auto names the GPU it picks. A CPU-only host is never shown a GPU option.
    public static IReadOnlyList<VoiceBackendPreferenceOption> BackendChoices(
        TranscriptionCapabilities capabilities, IReadOnlyList<GpuHardware>? gpus = null)
    {
        var selectable = capabilities.GpuUsable
            ? (gpus ?? []).Where(gpu => gpu.DeviceId is not null).ToList()
            : [];
        if (selectable.Count > 1)
        {
            var choices = new List<VoiceBackendPreferenceOption>
            {
                new($"Auto (recommended) — {_Name(GpuHardware.PickAuto(selectable))}", VoiceBackendPreference.Auto),
            };
            choices.AddRange(selectable.Select(gpu =>
                new VoiceBackendPreferenceOption($"GPU {_Name(gpu)}", GpuHardware.BackendFor(capabilities, gpu), gpu.DeviceId ?? "")));
            choices.Add(new("CPU", VoiceBackendPreference.Cpu));
            return choices;
        }

        return _SingleGpuChoices(capabilities);
    }

    // The saved choice as a dropdown entry: the exact GPU, else (a choice saved before GPUs were named) the first
    // entry on that backend, else Auto — so a GPU or runtime that is gone shows Auto rather than a dead entry.
    public static VoiceBackendPreferenceOption Find(
        IReadOnlyList<VoiceBackendPreferenceOption> choices, VoiceBackendPreference backend, string gpuDeviceId) =>
        choices.FirstOrDefault(option => option.Value == backend && option.GpuDeviceId == gpuDeviceId)
        ?? (string.IsNullOrEmpty(gpuDeviceId) ? choices.FirstOrDefault(option => option.Value == backend) : null)
        ?? choices[0];

    private static string _Name(GpuHardware gpu) => gpu.Description ?? gpu.Vendor.ToString();

    private static List<VoiceBackendPreferenceOption> _SingleGpuChoices(TranscriptionCapabilities capabilities)
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

    // A short badge describing the detected acceleration, so the choices read as host-aware.
    public static string HardwareBadge(TranscriptionCapabilities capabilities) => capabilities switch
    {
        { CudaUsable: true } => "NVIDIA CUDA GPU available",
        { VulkanUsable: true } => "Vulkan GPU available",
        _ => "No GPU acceleration detected — CPU only",
    };

    // One line explaining what the chosen backend does on this machine.
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
