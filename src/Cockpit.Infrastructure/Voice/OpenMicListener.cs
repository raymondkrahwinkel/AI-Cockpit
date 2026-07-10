using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="IOpenMicListener"/>: captures the microphone continuously, slices it into fixed analysis
/// windows, asks the VAD whether each window is speech, and feeds those observations to a
/// <see cref="VadEndpointDetector"/> to find utterance boundaries. On each detected utterance it runs STT
/// and raises <see cref="UtteranceTranscribed"/>. Registered as a singleton — one shared mic pipeline for
/// the whole (single-user) cockpit, mirroring <see cref="VoicePushToTalkService"/>.
/// </summary>
internal sealed class OpenMicListener(
    IAudioCaptureService captureService,
    IVoiceActivityDetector vad,
    ISpeechToTextService speechToText,
    IVoiceSettingsStore settingsStore,
    ILogger<OpenMicListener> logger)
    : IOpenMicListener, ISingletonService
{
    private static readonly AudioFormat CaptureFormat = new();

    // The mic is judged in fixed windows rather than per raw capture frame: Silero VAD needs a chunk of
    // a few tens of ms to decide speech, and the endpoint detector reasons in window-sized steps. 300ms
    // is a coarse-but-responsive default; the exact size is one of the values to tune live.
    private static readonly TimeSpan AnalysisWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinSpeechToStart = TimeSpan.FromMilliseconds(200);

    private static readonly int WindowByteCount =
        (int)(CaptureFormat.SampleRate * AnalysisWindow.TotalSeconds) * (CaptureFormat.BitsPerSample / 8);

    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private volatile bool _paused;

    public event EventHandler<string>? UtteranceTranscribed;
    public event EventHandler<double>? AudioLevelSampled;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loopTask is not null)
        {
            return;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var silenceTimeout = TimeSpan.FromMilliseconds(settings.OpenMicSilenceTimeoutMs);
        _cancellation = new CancellationTokenSource();
        _loopTask = _ListenAsync(silenceTimeout, _cancellation.Token);
        logger.LogInformation("Open-mic listening started (silence timeout {Timeout}ms)", settings.OpenMicSilenceTimeoutMs);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null || _loopTask is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loopTask = null;
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private async Task _ListenAsync(TimeSpan silenceTimeout, CancellationToken cancellationToken)
    {
        var detector = new VadEndpointDetector(silenceTimeout, MinSpeechToStart);
        var pending = new List<byte>();
        var utterance = new List<float>();
        float[]? preRoll = null;

        try
        {
            await foreach (var frame in captureService.CaptureAsync(CaptureFormat, cancellationToken).ConfigureAwait(false))
            {
                AudioLevelSampled?.Invoke(this, AudioLevelMeter.NormalizedRms(frame.Span));

                if (_paused)
                {
                    // Barge-in: abandon whatever was in progress so a resumed capture starts clean and the
                    // audio heard while read-aloud played is never transcribed.
                    detector.Reset();
                    pending.Clear();
                    utterance.Clear();
                    continue;
                }

                pending.AddRange(frame.ToArray());
                while (pending.Count >= WindowByteCount)
                {
                    var windowSamples = _ToFloatSamples(pending, WindowByteCount);
                    pending.RemoveRange(0, WindowByteCount);

                    var isSpeech = await vad.HasSpeechAsync(windowSamples, cancellationToken).ConfigureAwait(false);
                    switch (detector.Observe(isSpeech, AnalysisWindow))
                    {
                        case VadEndpointSignal.SpeechStarted:
                            utterance.Clear();
                            if (preRoll is not null)
                            {
                                // Prepend the window just before speech so the utterance's first phoneme is not clipped.
                                utterance.AddRange(preRoll);
                            }

                            utterance.AddRange(windowSamples);
                            break;

                        case VadEndpointSignal.None when detector.IsInSpeech:
                            utterance.AddRange(windowSamples);
                            break;

                        case VadEndpointSignal.SpeechEnded:
                            utterance.AddRange(windowSamples);
                            await _FinalizeUtteranceAsync([.. utterance], cancellationToken).ConfigureAwait(false);
                            utterance.Clear();
                            break;
                    }

                    preRoll = windowSamples;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: StopAsync cancels the capture stream.
        }
    }

    private async Task _FinalizeUtteranceAsync(float[] samples, CancellationToken cancellationToken)
    {
        var text = await speechToText.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            UtteranceTranscribed?.Invoke(this, text);
        }
    }

    private static float[] _ToFloatSamples(List<byte> pcmS16Bytes, int byteCount)
    {
        var sampleCount = byteCount / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s16 = (short)(pcmS16Bytes[i * 2] | (pcmS16Bytes[(i * 2) + 1] << 8));
            samples[i] = s16 / (float)short.MaxValue;
        }

        return samples;
    }
}
