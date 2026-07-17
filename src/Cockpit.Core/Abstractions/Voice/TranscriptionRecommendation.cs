using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>The GPU brand, as far as the host could be identified. Drives the recommendation: an NVIDIA card with
/// CUDA is the fast path, while an AMD/Intel card that also draws the screen is steered to the CPU.</summary>
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Apple,
}

/// <summary>
/// What the host's display GPU is, beyond "can a runtime load" (AC-68 slice 2): the brand, a human description,
/// whether that adapter also drives a monitor (so GPU transcription would fight the compositor for it), and its
/// dedicated VRAM. All best-effort — a field the probe could not determine is left at its neutral default, and
/// the recommender degrades to a safe CPU choice rather than guessing.
/// </summary>
public sealed record GpuHardware(GpuVendor Vendor, string? Description, bool DrivesDisplay, long VideoMemoryBytes)
{
    /// <summary>No GPU could be identified — the recommender then treats the machine as CPU-only.</summary>
    public static GpuHardware None { get; } = new(GpuVendor.Unknown, Description: null, DrivesDisplay: false, VideoMemoryBytes: 0);
}

/// <summary>
/// The advisor's pick for this machine (AC-68 slice 2): a concrete model and backend plus the human reason and
/// the short hardware badges the Transcribe page shows. <see cref="Backend"/> is what "Auto" resolves to — never
/// <see cref="VoiceBackendPreference.Auto"/> itself.
/// </summary>
public sealed record TranscriptionRecommendation(
    string Model,
    VoiceBackendPreference Backend,
    string Reason,
    IReadOnlyList<string> Badges);

/// <summary>
/// The hardware-aware rule table (AC-68 slice 2). Pure so it is unit-testable without probing a real machine:
/// given what can load, what the display GPU is, and the OS, it recommends a model + backend + reason. The
/// governing insight is that a single GPU which also draws the screen should transcribe on the CPU, so a long
/// dictation does not make the desktop stutter — the GPU is fast but it is busy being your display.
/// </summary>
public static class TranscriptionRecommender
{
    /// <summary>Below this, a discrete GPU is treated as too small to be worth the fast path even on NVIDIA.</summary>
    public const long MinGpuVramBytes = 6L * 1024 * 1024 * 1024;

    private const string FullModel = "large-v3-turbo";
    private const string LightModel = "small";

    public static TranscriptionRecommendation Recommend(
        TranscriptionCapabilities capabilities,
        GpuHardware gpu,
        WhisperHostPlatform? platform)
    {
        var badges = _Badges(capabilities, gpu, platform);

        // Apple Silicon: Whisper's GPU path (Metal) rides inside the bundled CPU runtime, so the "CPU" backend is
        // already the accelerated one — there is no separate GPU backend to pick.
        if (platform is WhisperHostPlatform.MacOs)
        {
            return new(FullModel, VoiceBackendPreference.Cpu,
                "Apple Silicon transcribes on the GPU through Metal, which rides inside the CPU runtime — nothing to switch.",
                badges);
        }

        // NVIDIA with real CUDA and enough VRAM: the fast path a dedicated card is for. NVIDIA cards are the case
        // where the GPU driving the display and transcribing at the same time is comfortably within budget.
        if (capabilities.CudaUsable && gpu.Vendor is GpuVendor.Nvidia
            && (gpu.VideoMemoryBytes == 0 || gpu.VideoMemoryBytes >= MinGpuVramBytes))
        {
            return new(FullModel, VoiceBackendPreference.Cuda,
                "Your NVIDIA GPU has CUDA and the memory for it — the fastest path here, and it keeps up while driving the display.",
                badges);
        }

        // Any usable GPU: use it. This is only the first-run guess before a calibration exists, and the CPU
        // alternative can be unusably slow (a full model can take tens of seconds on the CPU), so defaulting to the
        // fast path is far safer than defaulting to a slow one. If the GPU also draws the screen and a long
        // dictation stutters the desktop, the first-use calibration measures exactly that and moves Auto to the
        // CPU — a real number, not this guess. Vulkan preferred where present (the AMD/Intel path), else CUDA.
        if (capabilities.GpuUsable)
        {
            var backend = capabilities.VulkanUsable ? VoiceBackendPreference.Vulkan : VoiceBackendPreference.Cuda;
            var displayNote = gpu.DrivesDisplay
                ? " It also draws your screen, so run the first-use calibration to confirm dictation does not make the desktop stutter."
                : string.Empty;

            return new(FullModel, backend,
                $"A GPU is available, so Auto uses it for speed.{displayNote}",
                badges);
        }

        // No GPU acceleration at all: a lighter model keeps dictation responsive on the CPU.
        return new(LightModel, VoiceBackendPreference.Cpu,
            $"No GPU acceleration was detected, so a lighter {LightModel} keeps dictation responsive on the CPU.",
            badges);
    }

    private static IReadOnlyList<string> _Badges(TranscriptionCapabilities capabilities, GpuHardware gpu, WhisperHostPlatform? platform)
    {
        var badges = new List<string>();

        if (!string.IsNullOrWhiteSpace(gpu.Description))
        {
            badges.Add(gpu.Description!);
        }
        else if (gpu.Vendor is not GpuVendor.Unknown)
        {
            badges.Add(_BrandWord(gpu.Vendor));
        }

        if (gpu.DrivesDisplay)
        {
            badges.Add("drives display");
        }

        if (gpu.VideoMemoryBytes > 0)
        {
            badges.Add($"{gpu.VideoMemoryBytes / (1024 * 1024 * 1024)} GB VRAM");
        }

        if (platform is WhisperHostPlatform.MacOs)
        {
            badges.Add("Metal");
        }
        else
        {
            badges.Add(capabilities.CudaUsable ? "CUDA" : "no CUDA");
        }

        return badges;
    }

    private static string _BrandWord(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA GPU",
        GpuVendor.Amd => "AMD GPU",
        GpuVendor.Intel => "Intel GPU",
        GpuVendor.Apple => "Apple GPU",
        _ => "GPU",
    };
}
