using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

// AC-1013: Applies a resolved backend try-order to Whisper.net's `RuntimeOptions`, fetching a GPU runtime first
// if needed. The one place touching `RuntimeLibraryOrder`/`LibraryPath` together, so the two callers (normal
// provisioner, calibration child) cannot drift; `LibraryPath` may only point at the cache when a runtime is actually there — else the bundled CPU natives get hidden and dictation hard-fails.
internal static class WhisperRuntimeActivation
{
    public static async Task ApplyAsync(
        IReadOnlyList<WhisperRuntimeBackend> order,
        WhisperHostPlatform host,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IProgress<VoicePreparationProgress>? progress = null)
    {
        RuntimeOptions.RuntimeLibraryOrder = order.Select(WhisperRuntimeBackendMapping.ToNative).ToList();

        var cachedRuntimeAvailable = await WhisperRuntimeCache
            .EnsureAvailableAsync(order, host, cancellationToken, logger, progress)
            .ConfigureAwait(false);

        RuntimeOptions.LibraryPath = cachedRuntimeAvailable ? WhisperRuntimeCache.SearchPath : null;
    }
}
