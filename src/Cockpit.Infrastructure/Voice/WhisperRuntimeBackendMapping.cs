using Cockpit.Core.Voice;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

// Maps between Cockpit.Core's OS-agnostic `WhisperRuntimeBackend` and Whisper.net's own `RuntimeLibrary`.
internal static class WhisperRuntimeBackendMapping
{
    public static RuntimeLibrary ToNative(WhisperRuntimeBackend backend) => backend switch
    {
        WhisperRuntimeBackend.Cuda => RuntimeLibrary.Cuda,
        WhisperRuntimeBackend.Cuda12 => RuntimeLibrary.Cuda12,
        WhisperRuntimeBackend.Vulkan => RuntimeLibrary.Vulkan,
        WhisperRuntimeBackend.Cpu => RuntimeLibrary.Cpu,
        WhisperRuntimeBackend.CpuNoAvx => RuntimeLibrary.CpuNoAvx,
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unmapped Whisper runtime backend."),
    };

    // Null when the loaded library is a family Cockpit never offers (CoreML/OpenVino) — those cannot be selected via `WhisperBackendPlanner`, so nothing maps back to them.
    public static WhisperRuntimeBackend? FromNative(RuntimeLibrary library) => library switch
    {
        RuntimeLibrary.Cuda => WhisperRuntimeBackend.Cuda,
        RuntimeLibrary.Cuda12 => WhisperRuntimeBackend.Cuda12,
        RuntimeLibrary.Vulkan => WhisperRuntimeBackend.Vulkan,
        RuntimeLibrary.Cpu => WhisperRuntimeBackend.Cpu,
        RuntimeLibrary.CpuNoAvx => WhisperRuntimeBackend.CpuNoAvx,
        _ => null,
    };
}
