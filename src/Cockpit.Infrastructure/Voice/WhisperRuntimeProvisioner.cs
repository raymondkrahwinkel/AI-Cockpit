using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Settles which native runtime Whisper.net will load, and gets it onto disk, before <em>anything</em> builds a
/// Whisper factory. Runs once per process, whoever asks first.
/// <para>
/// It exists because <c>RuntimeOptions</c> is read exactly once — when the natives are loaded — and the first
/// thing to load them is not the obvious one. A push-to-talk hold gates its audio through the VAD before it
/// transcribes, so <c>WhisperVadFactory</c> is what actually pulls the natives in; by the time the STT service
/// set its options, the loader had already picked, and it silently kept whatever it found next to the exe.
/// While the GPU runtimes were bundled that was harmless (the VAD found the right one anyway). The moment they
/// became a fetch, it meant the GPU was downloaded, cached, and never used — the machine just transcribed
/// slowly. Both callers now come through here first.
/// </para>
/// </summary>
internal sealed class WhisperRuntimeProvisioner(
    IVoiceSettingsStore settingsStore, ILogger<WhisperRuntimeProvisioner> logger) : ISingletonService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _prepared;

    /// <summary>Progress on a first-use runtime fetch. Fires on the download's thread — subscribers marshal themselves.</summary>
    public event EventHandler<VoicePreparationProgress>? Preparing;

    /// <summary>
    /// Must be awaited before any <c>WhisperFactory</c> or <c>WhisperVadFactory</c> exists. Idempotent and safe
    /// to call from either side of a hold; the second caller waits for the first rather than racing it.
    /// </summary>
    public async Task EnsurePreparedAsync(CancellationToken cancellationToken)
    {
        if (_prepared)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prepared)
            {
                return;
            }

            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var progress = new ImmediateProgress<VoicePreparationProgress>(step => Preparing?.Invoke(this, step));

            var platform = WhisperRuntimeCache.CurrentPlatform;
            var order = platform is { } host
                ? WhisperBackendPlanner.BuildOrder(settings.BackendPreference, host)
                : [WhisperRuntimeBackend.Cpu];
            RuntimeOptions.RuntimeLibraryOrder = order.Select(WhisperRuntimeBackendMapping.ToNative).ToList();

            if (platform is { } fetchHost)
            {
                await WhisperRuntimeCache
                    .EnsureAvailableAsync(order, fetchHost, cancellationToken, logger, progress)
                    .ConfigureAwait(false);
                RuntimeOptions.LibraryPath = WhisperRuntimeCache.SearchPath;
            }

            // macOS only: Metal ships inside the bundled CPU runtime, but its shader has to be findable.
            WhisperMetalShader.EnsureDiscoverable(logger);

            _prepared = true;
        }
        finally
        {
            _lock.Release();
        }
    }
}
