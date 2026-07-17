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
/// Detects which Whisper acceleration backends this host can load, so the Options → Voice → Transcribe page
/// offers only host-relevant choices (no CUDA on a non-NVIDIA machine) and can explain the trade-off. This is
/// slice 1 of AC-68 — capability detection only. Later slices enrich the advice with GPU brand and
/// display-adapter facts (AC-68 slice 2) and a measured first-use calibration (slice 3).
/// </summary>
public interface ITranscriptionAdvisor
{
    /// <summary>Which GPU backends this machine can actually load. Cached after the first probe.</summary>
    TranscriptionCapabilities DetectCapabilities();
}
