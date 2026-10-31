using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

// The GPU brand, as far as the host could be identified. Drives the recommendation: an NVIDIA card with
// CUDA is the fast path, while an AMD/Intel card that also draws the screen is steered to the CPU.
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Apple,
}

// AC-1013: What the host's display GPU is, beyond "can a runtime load" (AC-68 slice 2) — brand, description,
// whether it also drives a monitor (so GPU transcription would fight the compositor), and dedicated VRAM.
// Trimmed: all best-effort — a field the probe can't determine stays at its neutral default, and the recommender degrades to a safe CPU choice rather than guessing.
public sealed record GpuHardware(GpuVendor Vendor, string? Description, bool DrivesDisplay, long VideoMemoryBytes)
{
    // No GPU could be identified — the recommender then treats the machine as CPU-only.
    public static GpuHardware None { get; } = new(GpuVendor.Unknown, Description: null, DrivesDisplay: false, VideoMemoryBytes: 0);

    public GpuKind Kind { get; init; }

    // AC-1881: The Vulkan device UUID — the stable key a GPU choice is saved under (indices shift between boots).
    // Null when the GPU was not seen through Vulkan, which makes it unselectable.
    public string? DeviceId { get; init; }

    // Position in vkEnumeratePhysicalDevices, the index GGML_VK_VISIBLE_DEVICES expects.
    public int? VulkanIndex { get; init; }

    // AC-1881: What "Auto" transcribes on: a discrete GPU before an integrated one, then one without a monitor
    // before the one drawing the desktop (on an Optimus laptop that is the iGPU), then the most VRAM.
    public static GpuHardware PickAuto(IReadOnlyList<GpuHardware> gpus) => gpus
        .OrderByDescending(gpu => gpu.Kind is GpuKind.Discrete)
        .ThenBy(gpu => gpu.DrivesDisplay)
        .ThenByDescending(gpu => gpu.VideoMemoryBytes)
        .FirstOrDefault() ?? None;

    // The saved choice when that GPU is still present, otherwise Auto — like a vanished microphone falls back to the default.
    public static GpuHardware Resolve(IReadOnlyList<GpuHardware> gpus, string? deviceId) =>
        gpus.FirstOrDefault(gpu => !string.IsNullOrEmpty(deviceId) && gpu.DeviceId == deviceId) ?? PickAuto(gpus);

    // The GPU backend to run on this GPU: CUDA only reaches NVIDIA cards, so a known other brand goes through Vulkan.
    public static VoiceBackendPreference BackendFor(TranscriptionCapabilities capabilities, GpuHardware gpu) =>
        capabilities.CudaUsable && (gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Unknown || !capabilities.VulkanUsable)
            ? VoiceBackendPreference.Cuda
            : VoiceBackendPreference.Vulkan;
}

public enum GpuKind
{
    Unknown,
    Integrated,
    Discrete,
}

// The advisor's pick for this machine (AC-68 slice 2): a concrete model and backend plus the human reason and
// the short hardware badges the Transcribe page shows. `Backend` is what "Auto" resolves to — never
// `VoiceBackendPreference.Auto` itself.
public sealed record TranscriptionRecommendation(
    string Model,
    VoiceBackendPreference Backend,
    string Reason,
    IReadOnlyList<string> Badges);

// AC-1013: The hardware-aware rule table (AC-68 slice 2); pure so it's unit-testable without probing a
// real machine. Trimmed: governing insight is a single GPU that also draws the screen should transcribe
// on the CPU, so a long dictation doesn't make the desktop stutter — the GPU is fast but busy being your display.
public static class TranscriptionRecommender
{
    // Below this, a discrete GPU is treated as too small to be worth the fast path even on NVIDIA.
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

        // AC-1013: Any usable GPU is used here as only the first-run guess before a calibration exists (CPU
        // can be unusably slow). Trimmed: why defaulting to fast-but-maybe-wrong beats slow-but-safe — a
        // display-driving GPU that stutters gets corrected to CPU once the first-use calibration has real numbers. Vulkan preferred where present, else CUDA.
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
            badges.Add(gpu.Description);
        }
        else if (gpu.Vendor is not GpuVendor.Unknown)
        {
            badges.Add(_BrandWord(gpu.Vendor));
        }

        if (gpu.DrivesDisplay)
        {
            badges.Add("drives display");
        }

        // An iGPU's carve-out is a few hundred MB; whole-GB division would print it as "0 GB".
        if (gpu.VideoMemoryBytes >= 1024L * 1024 * 1024)
        {
            badges.Add($"{gpu.VideoMemoryBytes / (1024 * 1024 * 1024)} GB VRAM");
        }
        else if (gpu.VideoMemoryBytes > 0)
        {
            badges.Add($"{gpu.VideoMemoryBytes / (1024 * 1024)} MB VRAM");
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
