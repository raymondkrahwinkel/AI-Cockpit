namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// What speech-to-text acceleration this machine can actually load right now. CPU is always available (the
/// runtime is bundled), so only the GPU paths are reported here — and each flag means "a real device answered
/// the probe", not merely "this OS could in principle publish the runtime". So a machine with no NVIDIA card
/// reports <see cref="CudaUsable"/> = false, which is how the Options dialog knows never to offer CUDA there.
/// </summary>
public sealed record TranscriptionCapabilities(bool CudaUsable, bool VulkanUsable)
{
    /// <summary>True when any GPU backend can load — i.e. a "GPU" option is worth offering at all.</summary>
    public bool GpuUsable => CudaUsable || VulkanUsable;

    /// <summary>Nothing but the bundled CPU runtime is available on this host.</summary>
    public static TranscriptionCapabilities CpuOnly { get; } = new(CudaUsable: false, VulkanUsable: false);
}

/// <summary>
/// Detects what this host can do for speech-to-text, so the Options → Voice → Transcribe page offers only
/// host-relevant choices (no CUDA on a non-NVIDIA machine), names the hardware, and can recommend a model +
/// backend with a reason. Slice 1 was capability detection; slice 2 adds the GPU brand / display-adapter facts
/// and the recommendation. A later slice measures a first-use calibration on top of the recommendation.
/// </summary>
public interface ITranscriptionAdvisor
{
    /// <summary>Which GPU backends this machine can actually load. Cached after the first probe.</summary>
    TranscriptionCapabilities DetectCapabilities();

    /// <summary>The display GPU's brand, description, whether it drives a monitor, and its VRAM (AC-68 slice 2).
    /// Best-effort; a field the host would not reveal stays at its neutral default. Cached after the first probe.</summary>
    GpuHardware DetectGpu();

    /// <summary>The hardware-aware model + backend pick for this machine, with the reason and badges (AC-68 slice 2).
    /// This is what "Auto" resolves to.</summary>
    TranscriptionRecommendation Recommend();
}
