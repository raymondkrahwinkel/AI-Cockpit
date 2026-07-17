using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Probes the host once for loadable Whisper GPU runtimes (AC-68 slice 1). It leans on the same
/// <see cref="WhisperGpuProbe"/> the runtime fetcher uses, so "usable" here means exactly what it means at
/// load time — a CUDA/Vulkan native library that loads and reports a device — never a guess from the OS alone.
/// GPU runtimes are only published off macOS, so a Mac (or an unknown OS) is reported CPU-only without probing.
/// </summary>
internal sealed class TranscriptionAdvisor : ITranscriptionAdvisor, ISingletonService
{
    private readonly object _gate = new();
    private TranscriptionCapabilities? _cached;

    public TranscriptionCapabilities DetectCapabilities()
    {
        // Probing loads native libraries (a quick NativeLibrary.TryLoad), so do it once and reuse — the answer
        // cannot change without a restart, and the Options dialog asks for it every time it opens.
        lock (_gate)
        {
            return _cached ??= _Probe();
        }
    }

    private static TranscriptionCapabilities _Probe()
    {
        var platform = WhisperRuntimeCache.CurrentPlatform;
        if (platform is null or WhisperHostPlatform.MacOs)
        {
            // macOS acceleration rides inside the bundled CPU runtime (Metal); there is no selectable GPU
            // backend to offer, and no CUDA/Vulkan package is published for it.
            return TranscriptionCapabilities.CpuOnly;
        }

        var cuda = WhisperGpuProbe.IsUsable(WhisperRuntimeBackend.Cuda)
                   || WhisperGpuProbe.IsUsable(WhisperRuntimeBackend.Cuda12);
        var vulkan = WhisperGpuProbe.IsUsable(WhisperRuntimeBackend.Vulkan);
        return new TranscriptionCapabilities(cuda, vulkan);
    }
}
