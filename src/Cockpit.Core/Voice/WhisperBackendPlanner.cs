namespace Cockpit.Core.Voice;

// Builds the ordered list of Whisper.net native runtimes to try for a given backend preference and host, so
// the cockpit stays hardware-agnostic (an NVIDIA box, an AMD one, an Apple Silicon Mac, a CPU-only server)
// without pinning a runtime at build time — which GPU a machine has is not knowable then. Whisper.net's own
// `NativeLibraryLoader` walks this list at model-load time and picks the first runtime that actually
// loads; this planner only decides the *order*, never which one wins. Every order ends in a CPU tail,
// so transcription never hard-fails for lack of a GPU.
// The orders say what a host *could* load, checked against the natives each NuGet package actually
// carries (verified against the real 1.9.1 packages, 2026-07-15) rather than against the README:
// - *Vulkan on Linux exists.* `Whisper.net.Runtime.Vulkan` 1.9.1 ships `linux-x64` natives
// beside `win-x64`. This planner used to call Vulkan Windows-only on the strength of issue #264, which
// cost every AMD-on-Linux host its GPU — in silence, because a missing runtime is one the loader skips.
// - *macOS has no CUDA, no Vulkan and no NoAvx* — none of those packages carry a macOS native. Its
// GPU path is Metal, which is not a `RuntimeLibrary` at all: it rides inside the CPU runtime
// (`libggml-metal-whisper.dylib`, `macos-arm64` only). So the Mac's honest order is the CPU tail
// alone — and on Apple Silicon that entry is already GPU-accelerated. An Intel Mac ships no Metal native and
// genuinely is on the CPU.
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
