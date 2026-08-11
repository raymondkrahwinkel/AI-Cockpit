using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

// `IVoicePushToTalkService`: buffers microphone audio for the duration of a hold, then on
// release gates it through VAD and transcribes. Registered as a singleton — this single-user desktop
// cockpit only ever has one hold in flight at a time.
internal sealed class VoicePushToTalkService(
    IAudioCaptureService captureService,
    IVoiceActivityDetector vad,
    ISpeechToTextService speechToText,
    ILogger<VoicePushToTalkService> logger)
    : IVoicePushToTalkService, ISingletonService
{
    private static readonly AudioFormat CaptureFormat = new();

    // AC-705: mirrors OpenMicListener's endpointing so a hold splits on trailing silence instead of
    // waiting for the whole recording. The hard cap covers a paragraph read with no pause — Whisper pads
    // every clip to a 30s window anyway, so an unbroken chunk is cut here rather than growing unbound.
    private static readonly TimeSpan ChunkSilenceTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan MinSpeechToStart = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ChunkHardCap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AnalysisWindow = TimeSpan.FromMilliseconds(300);

    private static readonly int WindowByteCount =
        (int)(CaptureFormat.SampleRate * AnalysisWindow.TotalSeconds) * (CaptureFormat.BitsPerSample / 8);

    private readonly PushToTalkHoldGuard _holdGuard = new();
    private CancellationTokenSource? _captureCancellation;
    private Task<CaptureResult>? _captureTask;

    // One hold's outcome: transcriptions already dispatched for chunks the hold closed on a silence (or the
    // hard cap) while it was still recording, in the order those chunks were recorded, plus whatever audio
    // was still open — the tail — when the key came up.
    private sealed record CaptureResult(List<Task<string>> ChunkTranscriptions, float[] TailSamples);

    public event EventHandler<double>? AudioLevelSampled;

    // Straight through from the STT service, so the views driving a hold do not each need their own handle on
    // it: they already have this interface, and this is one more thing a hold is doing.
    public event EventHandler<VoicePreparationProgress>? Preparing
    {
        add => speechToText.Preparing += value;
        remove => speechToText.Preparing -= value;
    }

    public event EventHandler? Prepared
    {
        add => speechToText.Prepared += value;
        remove => speechToText.Prepared -= value;
    }

    public bool BeginHold()
    {
        if (!_holdGuard.TryBeginHold())
        {
            return false;
        }

        _captureCancellation = new CancellationTokenSource();
        _captureTask = _CaptureAsync(_captureCancellation.Token);
        return true;
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        speechToText.WarmUpAsync(cancellationToken);

    public async Task<string> EndHoldAsync(CancellationToken cancellationToken = default)
    {
        if (_captureTask is null || _captureCancellation is null)
        {
            throw new InvalidOperationException($"{nameof(EndHoldAsync)} called without a preceding {nameof(BeginHold)}.");
        }

        await _captureCancellation.CancelAsync().ConfigureAwait(false);
        var capture = await _captureTask.ConfigureAwait(false);
        _holdGuard.Release();
        _captureTask = null;
        _captureCancellation.Dispose();
        _captureCancellation = null;

        try
        {
            // AC-705: chunks are appended in recording order, never completion order — a chunk that comes
            // back fast must never jump ahead of an earlier one still transcribing. Awaiting the list in
            // order guarantees that regardless of how many chunks the worker ends up running concurrently.
            var pieces = new List<string>(capture.ChunkTranscriptions.Count + 1);
            foreach (var chunkTranscription in capture.ChunkTranscriptions)
            {
                var chunkText = await chunkTranscription.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    pieces.Add(chunkText);
                }
            }

            if (capture.TailSamples.Length > 0 && await vad.HasSpeechAsync(capture.TailSamples, cancellationToken).ConfigureAwait(false))
            {
                var tailText = await speechToText.TranscribeAsync(capture.TailSamples, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tailText))
                {
                    pieces.Add(tailText);
                }
            }

            if (pieces.Count == 0)
            {
                logger.LogInformation("Push-to-talk hold produced no detected speech; discarding");
                return string.Empty;
            }

            return pieces.Count == 1 ? pieces[0] : string.Join(' ', pieces);
        }
        catch (Exception ex)
        {
            // VAD/STT can throw on a failed first-use model download (Whisper + Silero, fetched lazily) or a
            // native transcription fault. The caller (SessionPanelViewModel.EndVoiceHoldAsync) only shows a
            // "Voice error" status, so without a log line here the failure was invisible (looked like a dead hotkey).
            logger.LogError(ex, "Voice dictation failed after capture (VAD/STT)");
            throw;
        }
    }

    private async Task<CaptureResult> _CaptureAsync(CancellationToken cancellationToken)
    {
        var detector = new VadEndpointDetector(ChunkSilenceTimeout, MinSpeechToStart);
        var pending = new List<byte>();
        var chunkSamples = new List<float>();
        var chunkDuration = TimeSpan.Zero;
        var transcriptions = new List<Task<string>>();
        float[]? preRoll = null;

        try
        {
            await foreach (var frame in captureService.CaptureAsync(CaptureFormat, cancellationToken).ConfigureAwait(false))
            {
                AudioLevelSampled?.Invoke(this, AudioLevelMeter.NormalizedRms(frame.Span));
                pending.AddRange(frame.ToArray());

                while (pending.Count >= WindowByteCount)
                {
                    var windowSamples = _ToFloatSamples(pending, WindowByteCount);
                    pending.RemoveRange(0, WindowByteCount);

                    // Not tied to `cancellationToken`: a release landing mid-classification must never drop a
                    // window that was already captured off the mic — every completed window has to land
                    // somewhere, either in a chunk or in the tail below.
                    var isSpeech = await vad.HasSpeechAsync(windowSamples, CancellationToken.None).ConfigureAwait(false);
                    switch (detector.Observe(isSpeech, AnalysisWindow))
                    {
                        case VadEndpointSignal.SpeechStarted:
                            chunkSamples.Clear();
                            chunkDuration = TimeSpan.Zero;
                            if (preRoll is not null)
                            {
                                // Prepend the window just before speech so the chunk's first phoneme is not clipped.
                                chunkSamples.AddRange(preRoll);
                            }

                            chunkSamples.AddRange(windowSamples);
                            chunkDuration += AnalysisWindow;
                            break;

                        case VadEndpointSignal.None when detector.IsInSpeech:
                            chunkSamples.AddRange(windowSamples);
                            chunkDuration += AnalysisWindow;
                            if (chunkDuration >= ChunkHardCap)
                            {
                                _DispatchChunk(chunkSamples, transcriptions);
                                chunkSamples.Clear();
                                chunkDuration = TimeSpan.Zero;
                            }

                            break;

                        case VadEndpointSignal.SpeechEnded:
                            chunkSamples.AddRange(windowSamples);
                            _DispatchChunk(chunkSamples, transcriptions);
                            chunkSamples.Clear();
                            chunkDuration = TimeSpan.Zero;
                            break;
                    }

                    preRoll = windowSamples;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: EndHoldAsync cancels the capture stream when the hotkey is released.
        }

        // Whatever never closed into a chunk: the words spoken right up to release, plus any trailing bytes
        // too short to fill one more analysis window. Only this still needs transcribing after the key comes up.
        chunkSamples.AddRange(_ToFloatSamples(pending));
        return new CaptureResult(transcriptions, [.. chunkSamples]);
    }

    // Fired with no cancellation tied to the hold (AC-705): once a chunk closes it is committed, so releasing
    // the key must never discard an encoder pass already dispatched to the worker.
    private void _DispatchChunk(List<float> chunkSamples, List<Task<string>> transcriptions) =>
        transcriptions.Add(speechToText.TranscribeAsync([.. chunkSamples], CancellationToken.None));

    private static float[] _ToFloatSamples(List<byte> pcmS16Bytes) => _ToFloatSamples(pcmS16Bytes, pcmS16Bytes.Count);

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
