using System.Diagnostics;
using SherpaOnnx;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

// ITextToSpeechService backed by sherpa-onnx running the multilingual, multi-speaker SupertonicTTS
// model. Singleton — loaded once and reused across every session/language. Model is downloaded/cached
// via SupertonicModelCache; one fixed-sid voice covers every language, so mixed-language replies never switch voice.
internal sealed class SherpaOnnxTextToSpeechService(IVoiceSettingsStore settingsStore, ILogger<SherpaOnnxTextToSpeechService> logger)
    : ITextToSpeechService, ISingletonService
{
    // Sherpa-onnx takes whatever it is given, including 0 or negative values that would never produce audible
    // speech (or could misbehave in the native call) — clamped here so a corrupt/hand-edited config can't reach it.
    private const float MinSpeed = 0.5f;
    private const float MaxSpeed = 2.0f;

    private OfflineTts? _tts;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // GenerateWithConfig requires a non-null progress callback (it pins the delegate); this no-op returns 1
    // to tell the native engine to keep generating. One shared instance — the callback is stateless.
    private static readonly OfflineTtsCallbackProgressWithArg ContinueGenerating = (_, _, _, _) => 1;

    // Split out from `SynthesizeAsync` so the clamp is unit-testable without the native TTS model.
    internal static float ClampSpeed(double speed) => Math.Clamp((float)speed, MinSpeed, MaxSpeed);

    public async Task<TtsAudio> SynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken = default)
    {
        var tts = await _GetOrLoadModelAsync(cancellationToken).ConfigureAwait(false);
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var config = new OfflineTtsGenerationConfig { Speed = ClampSpeed(settings.TtsSpeed), Sid = speakerId };
        // Supertonic reads the target language from the generation config's "extra" bag (serialized to JSON
        // for the native call); it is what lets one voice pronounce each segment in its tagged language.
        config.Extra["lang"] = language;

        // sherpa-onnx's OfflineTts.GenerateWithConfig is a synchronous, CPU-bound native call — run it off
        // the calling (UI/consumer) thread so it never blocks the playback queue's own async loop.
        var stopwatch = Stopwatch.StartNew();
        var audio = await Task.Run(() => tts.GenerateWithConfig(text, config, ContinueGenerating), cancellationToken)
            .ConfigureAwait(false);

        // AC-535: per-utterance synthesis trace — the playback queue logs its own play/abort side separately.
        logger.LogDebug("TTS synthesized {Length} chars in {ElapsedMs} ms ({SampleCount} samples at {SampleRate} Hz).",
            text.Length, stopwatch.ElapsedMilliseconds, audio.Samples.Length, audio.SampleRate);

        return new TtsAudio(audio.Samples, audio.SampleRate);
    }

    // Loads the voice ahead of the reply that is coming (AC-603), and says nothing when it cannot.
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _GetOrLoadModelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Warming the voice failed; the next spoken reply will try again.");
        }
    }

    private async Task<OfflineTts> _GetOrLoadModelAsync(CancellationToken cancellationToken)
    {
        if (_tts is not null)
        {
            return _tts;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tts is not null)
            {
                return _tts;
            }

            logger.LogInformation("Loading SupertonicTTS model (downloading on first use if not cached)...");
            var stopwatch = Stopwatch.StartNew();
            var paths = await SupertonicModelCache.EnsureDownloadedAsync(cancellationToken).ConfigureAwait(false);

            var config = new OfflineTtsConfig
            {
                Model = new OfflineTtsModelConfig
                {
                    Supertonic = new OfflineTtsSupertonicModelConfig
                    {
                        DurationPredictor = paths.DurationPredictorPath,
                        TextEncoder = paths.TextEncoderPath,
                        VectorEstimator = paths.VectorEstimatorPath,
                        Vocoder = paths.VocoderPath,
                        TtsJson = paths.TtsJsonPath,
                        UnicodeIndexer = paths.UnicodeIndexerPath,
                        VoiceStyle = paths.VoiceStylePath,
                    },
                    NumThreads = 1,
                    Provider = "cpu",
                },
            };

            _tts = new OfflineTts(config);
            // The one truly expensive step (AC-535): it happens once per app run, so its cost is worth its own line
            // rather than being folded into the first utterance's synthesis time.
            logger.LogInformation("SupertonicTTS model loaded in {ElapsedMs} ms.", stopwatch.ElapsedMilliseconds);
            return _tts;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
