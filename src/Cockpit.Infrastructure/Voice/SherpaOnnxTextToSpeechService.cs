using SherpaOnnx;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ITextToSpeechService"/> backed by sherpa-onnx running the multilingual, multi-speaker
/// SupertonicTTS model. Registered as a singleton — in this single-user desktop cockpit the model is loaded
/// once and reused across every session and language, so no utterance pays the (real) load cost again. The
/// model is downloaded and cached on first use via <see cref="SupertonicModelCache"/>, mirroring the Whisper
/// model's lazy download. One voice covers every language: the speaker (timbre) is a fixed sid and the
/// language is passed per utterance as generation data, so a mixed Dutch/English reply never switches voice.
/// </summary>
internal sealed class SherpaOnnxTextToSpeechService(ILogger<SherpaOnnxTextToSpeechService> logger)
    : ITextToSpeechService, ISingletonService
{
    private OfflineTts? _tts;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // GenerateWithConfig requires a non-null progress callback (it pins the delegate); this no-op returns 1
    // to tell the native engine to keep generating. One shared instance — the callback is stateless.
    private static readonly OfflineTtsCallbackProgressWithArg ContinueGenerating = (_, _, _, _) => 1;

    public async Task<TtsAudio> SynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken = default)
    {
        var tts = await _GetOrLoadModelAsync(cancellationToken).ConfigureAwait(false);

        var config = new OfflineTtsGenerationConfig { Speed = 1.0f, Sid = speakerId };
        // Supertonic reads the target language from the generation config's "extra" bag (serialized to JSON
        // for the native call); it is what lets one voice pronounce each segment in its tagged language.
        config.Extra["lang"] = language;

        // sherpa-onnx's OfflineTts.GenerateWithConfig is a synchronous, CPU-bound native call — run it off
        // the calling (UI/consumer) thread so it never blocks the playback queue's own async loop.
        var audio = await Task.Run(() => tts.GenerateWithConfig(text, config, ContinueGenerating), cancellationToken)
            .ConfigureAwait(false);

        return new TtsAudio(audio.Samples, audio.SampleRate);
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
            return _tts;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
