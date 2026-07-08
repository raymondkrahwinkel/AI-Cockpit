using SherpaOnnx;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ITextToSpeechService"/> backed by sherpa-onnx running a Piper VITS voice. Registered as a
/// singleton — in this single-user desktop cockpit, loading each voice model once and reusing it across
/// every session avoids reloading it (a real cost) on every utterance. A voice's model is downloaded and
/// cached on first use via <see cref="PiperVoiceCache"/>, mirroring the Whisper model's lazy download.
/// </summary>
internal sealed class SherpaOnnxTextToSpeechService(ILogger<SherpaOnnxTextToSpeechService> logger)
    : ITextToSpeechService, ISingletonService
{
    private readonly Dictionary<string, OfflineTts> _loadedVoices = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public async Task<TtsAudio> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        var tts = await _GetOrLoadVoiceAsync(voiceId, cancellationToken).ConfigureAwait(false);

        // sherpa-onnx's OfflineTts.Generate is a synchronous, CPU/GPU-bound native call — run it off
        // the calling (UI/consumer) thread so it never blocks the playback queue's own async loop.
        var audio = await Task.Run(() => tts.Generate(text, speed: 1.0f, speakerId: 0), cancellationToken)
            .ConfigureAwait(false);

        return new TtsAudio(audio.Samples, audio.SampleRate);
    }

    private async Task<OfflineTts> _GetOrLoadVoiceAsync(string voiceId, CancellationToken cancellationToken)
    {
        if (_loadedVoices.TryGetValue(voiceId, out var cached))
        {
            return cached;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedVoices.TryGetValue(voiceId, out cached))
            {
                return cached;
            }

            logger.LogInformation("Loading Piper voice {VoiceId} (downloading on first use if not cached)...", voiceId);
            var paths = await PiperVoiceCache.EnsureDownloadedAsync(voiceId, cancellationToken).ConfigureAwait(false);

            var config = new OfflineTtsConfig
            {
                Model = new OfflineTtsModelConfig
                {
                    Vits = new OfflineTtsVitsModelConfig
                    {
                        Model = paths.ModelPath,
                        Tokens = paths.TokensPath,
                        DataDir = paths.DataDirectoryPath,
                    },
                    NumThreads = 1,
                    Provider = "cpu",
                },
            };

            var tts = new OfflineTts(config);
            _loadedVoices[voiceId] = tts;
            return tts;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
