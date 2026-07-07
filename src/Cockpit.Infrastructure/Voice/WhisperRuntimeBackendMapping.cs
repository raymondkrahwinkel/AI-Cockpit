using Cockpit.Core.Voice;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Maps between Cockpit.Core's OS-agnostic <see cref="WhisperRuntimeBackend"/> and Whisper.net's own <see cref="RuntimeLibrary"/>.</summary>
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

    /// <summary>Null when the loaded library is a family Cockpit never offers (CoreML/OpenVino) — those cannot be selected via <see cref="WhisperBackendPlanner"/>, so nothing maps back to them.</summary>
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
