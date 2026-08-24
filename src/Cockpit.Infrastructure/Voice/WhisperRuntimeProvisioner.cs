using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

// AC-1013: Settles which native runtime Whisper.net will load before anything builds a Whisper factory, because
// `RuntimeOptions` is read once and the VAD factory (not the STT service) is actually first to load it.
// Trimmed: the GPU-fetch regression story (bundled runtimes were harmless, a fetched GPU was silently unused).
internal sealed class WhisperRuntimeProvisioner(
    IVoiceSettingsStore settingsStore,
    ITranscriptionAdvisor advisor,
    ITranscriptionCalibrationStore calibrationStore,
    ILogger<WhisperRuntimeProvisioner> logger) : ISingletonService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _prepared;

    // Progress on a first-use runtime fetch. Fires on the download's thread — subscribers marshal themselves.
    public event EventHandler<VoicePreparationProgress>? Preparing;

    // Must be awaited before any `WhisperFactory` or `WhisperVadFactory` exists. Idempotent and safe
    // to call from either side of a hold; the second caller waits for the first rather than racing it.
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

            // "Auto" resolves to what this machine measured, if calibrated (AC-68); calibration times every
            // usable backend and overrules the rule-table guess with real numbers. Before calibration, the
            // recommendation is the best first guess. An explicit CPU/GPU choice is honoured as-is.
            var preference = settings.BackendPreference;
            if (preference is VoiceBackendPreference.Auto)
            {
                preference = await _ResolveAutoAsync(cancellationToken).ConfigureAwait(false);
            }

            var platform = WhisperRuntimeCache.CurrentPlatform;
            var order = platform is { } host
                ? WhisperBackendPlanner.BuildOrder(preference, host)
                : [WhisperRuntimeBackend.Cpu];

            if (platform is { } fetchHost)
            {
                await WhisperRuntimeActivation
                    .ApplyAsync(order, fetchHost, cancellationToken, logger, progress)
                    .ConfigureAwait(false);
            }
            else
            {
                RuntimeOptions.RuntimeLibraryOrder = order.Select(WhisperRuntimeBackendMapping.ToNative).ToList();
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

    // What "Auto" runs on: this machine's measured calibration verdict if it has one, otherwise the rule-table
    // recommendation. The calibration is the authority — it timed the backends here — so a stored choice wins over
    // the guess; the recommendation only fills in until the operator runs a calibration.
    private async Task<VoiceBackendPreference> _ResolveAutoAsync(CancellationToken cancellationToken)
    {
        var calibration = await calibrationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (calibration is { ChosenBackend: var chosen } && chosen is not VoiceBackendPreference.Auto)
        {
            // Trust the measured verdict — unless it points at a GPU this machine can no longer load (the card or
            // its driver went away since calibration). Then fall through to a fresh recommendation rather than
            // pinning Auto to a backend that would silently fall back to the CPU tail anyway.
            var capabilities = advisor.DetectCapabilities();
            var stillUsable = chosen switch
            {
                VoiceBackendPreference.Cuda => capabilities.CudaUsable,
                VoiceBackendPreference.Vulkan => capabilities.VulkanUsable,
                _ => true,
            };

            if (stillUsable)
            {
                logger.LogInformation("Transcription Auto resolved to {Backend} from this machine's calibration", chosen);

                return chosen;
            }

            logger.LogInformation(
                "This machine's calibration chose {Backend}, but it no longer loads here; falling back to the recommendation", chosen);
        }

        var recommendation = advisor.Recommend();
        logger.LogInformation(
            "Transcription Auto resolved to {Backend} on this machine (rule-table guess; not yet calibrated) — {Reason}",
            recommendation.Backend, recommendation.Reason);

        return recommendation.Backend;
    }
}
