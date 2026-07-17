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
    IVoiceSettingsStore settingsStore,
    ITranscriptionAdvisor advisor,
    ITranscriptionCalibrationStore calibrationStore,
    ILogger<WhisperRuntimeProvisioner> logger) : ISingletonService
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

            // "Auto" resolves to what this machine measured, if it has been calibrated (AC-68): the calibration
            // times every usable backend and picks one with a CPU preference, so it overrules the rule-table guess
            // with real numbers. Before any calibration, the recommendation is the best first guess. An explicit
            // CPU/GPU choice is honoured as-is.
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

    /// <summary>
    /// What "Auto" runs on: this machine's measured calibration verdict if it has one, otherwise the rule-table
    /// recommendation. The calibration is the authority — it timed the backends here — so a stored choice wins over
    /// the guess; the recommendation only fills in until the operator runs a calibration.
    /// </summary>
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
