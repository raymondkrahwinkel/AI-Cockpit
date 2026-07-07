namespace Cockpit.Core.Voice;

/// <summary>
/// The native Whisper.net runtime families the cockpit can load, mirroring
/// <c>Whisper.net.LibraryLoader.RuntimeLibrary</c> without leaking that dependency into Core (only
/// Infrastructure references Whisper.net). <see cref="WhisperRuntimeBackendMapping"/> in Infrastructure
/// maps between the two.
/// </summary>
public enum WhisperRuntimeBackend
{
    Cuda,
    Cuda12,
    Vulkan,
    Cpu,
    CpuNoAvx,
}
