using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

// `IOpenMicListener`: captures the microphone continuously, slices it into fixed analysis
// windows, asks the VAD whether each window is speech, and feeds those observations to a
// `VadEndpointDetector` to find utterance boundaries. On each detected utterance it runs STT
// and raises `UtteranceTranscribed`. Registered as a singleton — one shared mic pipeline for
// the whole (single-user) cockpit, mirroring `VoicePushToTalkService`.
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

    // AC-628: the guard below only holds while this is held — without it, concurrent starts each opened a microphone.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    // AC-628: a set rather than the single pair it will always be, so a stop cannot silently close one of several.
    private readonly List<(CancellationTokenSource Cancellation, Task Loop)> _running = [];

    private volatile bool _paused;

    public event EventHandler<string>? UtteranceTranscribed;
    public event EventHandler? SpeechStarted;
    public event EventHandler? SpeechEnded;
    public event EventHandler<double>? AudioLevelSampled;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running.Count > 0)
            {
                // AC-628: a start that does nothing left no trace, so four of them read exactly like one.
                logger.LogInformation("Open-mic was already listening; this start was ignored.");
                return;
            }

            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var silenceTimeout = TimeSpan.FromMilliseconds(settings.OpenMicSilenceTimeoutMs);
            var cancellation = new CancellationTokenSource();
            _running.Add((cancellation, _ListenAsync(silenceTimeout, cancellation.Token)));
            logger.LogInformation("Open-mic listening started (silence timeout {Timeout}ms)", settings.OpenMicSilenceTimeoutMs);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running.Count == 0)
            {
                return;
            }

            var running = _running.ToArray();
            _running.Clear();

            foreach (var (cancellation, loop) in running)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                cancellation.Dispose();
            }

            // AC-628: the log carried starts and no stops, so a mic left open read like one that was closed.
            logger.LogInformation("Open-mic listening stopped ({Loops} loop(s) closed).", running.Length);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private async Task _ListenAsync(TimeSpan silenceTimeout, CancellationToken cancellationToken)
    {
        var detector = new VadEndpointDetector(silenceTimeout, MinSpeechToStart);
        var pending = new List<byte>();
        var utterance = new List<float>();
        float[]? preRoll = null;

        // AC-721: finished utterances wait here to be transcribed one at a time, in speaking order — the same
        // fix OpenMicCoordinator._ConsumeInjectionsAsync already applies one layer up, for the same reason.
        var pendingUtterances = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
        var consumerTask = _ConsumeUtterancesAsync(pendingUtterances.Reader, cancellationToken);

        try
        {
            await foreach (var frame in captureService.CaptureAsync(CaptureFormat, cancellationToken).ConfigureAwait(false))
            {
                AudioLevelSampled?.Invoke(this, AudioLevelMeter.NormalizedRms(frame.Span));

                if (_paused)
                {
                    // Barge-in: abandon whatever was in progress so a resumed capture starts clean and the
                    // audio heard while read-aloud played is never transcribed.
                    //
                    // An utterance abandoned here never reaches SpeechEnded, so anyone told it started has to be
                    // told it is over — or the overlay sits on "Listening" for a sentence that will never be
                    // transcribed, which is the pill lying about a microphone that is not even being read.
                    if (detector.IsInSpeech)
                    {
                        SpeechEnded?.Invoke(this, EventArgs.Empty);
                    }

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

                            // Said out loud so the overlay can appear here — the boundary was already known and
                            // kept to itself, which is why open-mic dictated invisibly.
                            SpeechStarted?.Invoke(this, EventArgs.Empty);
                            break;

                        case VadEndpointSignal.None when detector.IsInSpeech:
                            utterance.AddRange(windowSamples);
                            break;

                        case VadEndpointSignal.SpeechEnded:
                            utterance.AddRange(windowSamples);

                            // Before transcribing, not after: transcription is the part worth showing a spinner
                            // for, and announcing the end once it finished would be announcing it too late.
                            SpeechEnded?.Invoke(this, EventArgs.Empty);

                            // AC-707: queuing here (not awaiting transcription) keeps the capture loop
                            // non-blocking, so it never stalls behind Whisper and never drops frames.
                            // Ordering is the consumer's job (AC-721).
                            pendingUtterances.Writer.TryWrite([.. utterance]);
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
        finally
        {
            pendingUtterances.Writer.TryComplete();
            try
            {
                await consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: StopAsync cancelled a clip that was still being transcribed when the mic stopped.
            }
        }
    }

    private async Task _ConsumeUtterancesAsync(ChannelReader<float[]> pendingUtterances, CancellationToken cancellationToken)
    {
        await foreach (var samples in pendingUtterances.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _FinalizeUtteranceAsync(samples, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task _FinalizeUtteranceAsync(float[] samples, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await speechToText.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: StopAsync cancelled this clip mid-transcription. Caught here, not left to propagate,
            // so the consumer loop above does not treat a cancelled clip as a reason to stop draining others.
            return;
        }

        // Raised even when the transcript filtered down to nothing (a throat-clear or a bare "um" the noise filter
        // removed): the overlay was flipped to "Transcribing" on SpeechEnded, and the coordinator clears it when the
        // utterance completes and drops the empty text — suppressing the empty case left that pill stuck spinning.
        UtteranceTranscribed?.Invoke(this, text);
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
