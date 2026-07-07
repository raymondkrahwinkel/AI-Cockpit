namespace Cockpit.Core.Voice;

/// <summary>
/// Builds the ordered list of Whisper.net native runtimes to try for a given backend preference and
/// OS, so the cockpit stays hardware-agnostic (RTX-4070 Fedora laptop, an AMD desktop, or a CPU-only
/// box) without pinning a single CUDA/Vulkan runtime at build time (research:
/// Cockpit-DotNet-Voice-Stack-2026-07-07.md §1). Whisper.net's own <c>NativeLibraryLoader</c> walks
/// this list at model-load time and picks the first runtime that actually loads on the host machine —
/// this planner only decides the *order*, never which one wins; that always includes a CPU tail so
/// transcription never hard-fails for lack of a GPU.
/// </summary>
/// <remarks>
/// Confirmed via NuGet + the whisper.net GitHub README (2026-07-07): <c>Whisper.net.Runtime.Vulkan</c>
/// is published for Windows x64 only — there is no Linux Vulkan runtime today (open issue
/// sandrohanea/whisper.net#264). So an explicit <see cref="VoiceBackendPreference.Vulkan"/> choice on
/// Linux has nothing to try and falls straight to CPU; it is not an error, just a gap this planner is
/// honest about instead of silently reordering to CUDA.
/// </remarks>
public static class WhisperBackendPlanner
{
    private static readonly WhisperRuntimeBackend[] CpuTail = [WhisperRuntimeBackend.Cpu, WhisperRuntimeBackend.CpuNoAvx];

    public static IReadOnlyList<WhisperRuntimeBackend> BuildOrder(VoiceBackendPreference preference, bool isWindows) =>
        preference switch
        {
            VoiceBackendPreference.Cpu => CpuTail,
            VoiceBackendPreference.Cuda => [WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, .. CpuTail],
            VoiceBackendPreference.Vulkan when isWindows => [WhisperRuntimeBackend.Vulkan, .. CpuTail],
            // No published Linux Vulkan runtime (see remarks) — fall straight to the CPU tail rather
            // than silently substituting a different GPU backend the operator did not ask for.
            VoiceBackendPreference.Vulkan => CpuTail,
            _ => isWindows
                ? [WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, WhisperRuntimeBackend.Vulkan, .. CpuTail]
                : [WhisperRuntimeBackend.Cuda, WhisperRuntimeBackend.Cuda12, .. CpuTail],
        };
}
