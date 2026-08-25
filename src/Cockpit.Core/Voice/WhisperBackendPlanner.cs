namespace Cockpit.Core.Voice;

// AC-1013: Builds the ordered list of Whisper.net native runtimes to try (host-agnostic, no build-time GPU pin); the loader picks the first that loads, every order ends in a CPU tail, and the orders reflect the natives actually shipped in the 1.9.1 packages (Vulkan on Linux, Mac limited to CPU/Metal) rather than the README.
public static class WhisperBackendPlanner
{
    public static IReadOnlyList<WhisperRuntimeBackend> BuildOrder(VoiceBackendPreference preference, WhisperHostPlatform platform)
    {
        var cpuTail = _CpuTail(platform);

        return preference switch
        {
            VoiceBackendPreference.Cpu => cpuTail,
            VoiceBackendPreference.Cuda when _HasDiscreteGpuRuntimes(platform) =>
                [WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, .. cpuTail],
            VoiceBackendPreference.Vulkan when _HasDiscreteGpuRuntimes(platform) =>
                [WhisperRuntimeBackend.Vulkan, .. cpuTail],
            // An explicit CUDA or Vulkan choice on a Mac has nothing to try. Fall to the CPU tail rather than
            // silently substituting a backend the operator did not ask for — on Apple Silicon that tail is
            // Metal-backed anyway, so the honest answer is also the fast one.
            VoiceBackendPreference.Cuda or VoiceBackendPreference.Vulkan => cpuTail,
            _ => _HasDiscreteGpuRuntimes(platform)
                ? [WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, WhisperRuntimeBackend.Vulkan, .. cpuTail]
                : cpuTail,
        };
    }

    // Whether CUDA/Vulkan runtimes are published for this host at all. macOS is the only one they are not,
    // and it is the odd one twice over: its GPU acceleration rides inside the CPU runtime instead of being a
    // family of its own.
    private static bool _HasDiscreteGpuRuntimes(WhisperHostPlatform platform) => platform is not WhisperHostPlatform.MacOs;

    // The universal fallback. `Whisper.net.Runtime.NoAvx` publishes `win-x64`, `win-x86` and
    // `linux-x64` natives and nothing for macOS, so listing it there would promise a runtime that cannot
    // be found — an entry the loader skips without a word.
    private static WhisperRuntimeBackend[] _CpuTail(WhisperHostPlatform platform) =>
        platform is WhisperHostPlatform.MacOs
            ? [WhisperRuntimeBackend.Cpu]
            : [WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx];
}
