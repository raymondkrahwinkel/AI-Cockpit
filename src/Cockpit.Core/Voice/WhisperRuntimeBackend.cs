namespace Cockpit.Core.Voice;

// The native Whisper.net runtime families the cockpit can load, mirroring
// `Whisper.net.LibraryLoader.RuntimeLibrary` without leaking that dependency into Core (only
// Infrastructure references Whisper.net). `WhisperRuntimeBackendMapping` in Infrastructure
// maps between the two.
public enum WhisperRuntimeBackend
{
    Cuda,
    Cuda12,
    Vulkan,
    Cpu,
    CpuNoAvx,
}
