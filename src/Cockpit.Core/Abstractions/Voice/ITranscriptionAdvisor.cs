namespace Cockpit.Core.Abstractions.Voice;

// AC-1013: Speech-to-text acceleration this machine can actually load. CPU is always available (bundled),
// so only GPU paths are reported. Trimmed: each flag means "a real device answered the probe", not "this
// OS could in principle publish the runtime" — a machine with no NVIDIA card reports CudaUsable = false.
public sealed record TranscriptionCapabilities(bool CudaUsable, bool VulkanUsable)
{
    // True when any GPU backend can load — i.e. a "GPU" option is worth offering at all.
    public bool GpuUsable => CudaUsable || VulkanUsable;

    // Nothing but the bundled CPU runtime is available on this host.
    public static TranscriptionCapabilities CpuOnly { get; } = new(CudaUsable: false, VulkanUsable: false);
}

/// <summary>
/// Detects what this host can do for speech-to-text, so Options → Voice → Transcribe offers only host-relevant
/// choices (no CUDA on a non-NVIDIA machine), names the hardware, and recommends a model + backend with a reason.
/// Slice 1 was detection; slice 2 adds GPU brand/adapter facts + recommendation; a later slice adds calibration.
/// </summary>
public interface ITranscriptionAdvisor
{
    /// <summary>
    /// Which GPU backends this machine can actually load. Cached after the first probe.
    /// </summary>
    TranscriptionCapabilities DetectCapabilities();

    /// <summary>The display GPU's brand, description, whether it drives a monitor, and its VRAM (AC-68 slice 2).
    /// Best-effort; a field the host would not reveal stays at its neutral default. Cached after the first probe.</summary>
    GpuHardware DetectGpu();

    /// <summary>The hardware-aware model + backend pick for this machine, with the reason and badges (AC-68 slice 2).
    /// This is what "Auto" resolves to.</summary>
    TranscriptionRecommendation Recommend();
}
