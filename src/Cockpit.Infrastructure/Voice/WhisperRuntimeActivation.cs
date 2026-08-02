using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

// Applies a resolved Whisper backend try-order to Whisper.net's global `RuntimeOptions`, fetching a
// GPU runtime onto disk first if the order calls for one. The one place that touches `RuntimeLibraryOrder`
// and `LibraryPath` together, so the two callers that must agree — the runtime provisioner on a normal hold,
// and the calibration child process measuring a forced backend — cannot drift.
//
// The subtlety it centralises: `LibraryPath` may point at the fetch cache *only* when a GPU runtime
// actually lives there. Whisper.net searches only that path once it is set, so pointing it at a cache that holds
// no runtime for this order would hide the bundled CPU natives beside the exe and hard-fail dictation with
// "native library not found" instead of falling back to the CPU floor.
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
