using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="IVoicePushToTalkService"/>: buffers microphone audio for the duration of a hold, then on
/// release gates it through VAD, transcribes, and optionally cleans up. Registered as a singleton — in
/// this single-user desktop cockpit only one session can hold the push-to-talk hotkey at a time (the
/// one with keyboard focus), so one shared hold/capture pipeline is all that is ever needed.
/// </summary>
internal sealed class VoicePushToTalkService(
    IAudioCaptureService captureService,
    IVoiceActivityDetector vad,
    ISpeechToTextService speechToText,
    ITranscriptCleanupService cleanup,
    ILogger<VoicePushToTalkService> logger)
    : IVoicePushToTalkService, ISingletonService
{
    private static readonly AudioFormat CaptureFormat = new();

    private readonly PushToTalkHoldGuard _holdGuard = new();
    private CancellationTokenSource? _captureCancellation;
    private Task<List<byte>>? _captureTask;

    public event EventHandler<double>? AudioLevelSampled;

    /// <summary>
    /// Straight through from the STT service, so the views driving a hold do not each need their own handle on
    /// it: they already have this interface, and this is one more thing a hold is doing.
    /// </summary>
    public event EventHandler<VoicePreparationProgress>? Preparing
    {
        add => speechToText.Preparing += value;
        remove => speechToText.Preparing -= value;
    }

    /// <inheritdoc/>
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

    public async Task<string> EndHoldAsync(bool applyCleanup, CancellationToken cancellationToken = default)
    {
        if (_captureTask is null || _captureCancellation is null)
        {
            throw new InvalidOperationException($"{nameof(EndHoldAsync)} called without a preceding {nameof(BeginHold)}.");
        }

        await _captureCancellation.CancelAsync().ConfigureAwait(false);
        var pcmBytes = await _captureTask.ConfigureAwait(false);
        _holdGuard.Release();
        _captureTask = null;
        _captureCancellation.Dispose();
        _captureCancellation = null;

        var samples = _ToFloatSamples(pcmBytes);
        try
        {
            if (samples.Length == 0 || !await vad.HasSpeechAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Push-to-talk hold produced no detected speech; discarding");
                return string.Empty;
            }

            var raw = await speechToText.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            return applyCleanup ? await cleanup.CleanupAsync(raw, cancellationToken).ConfigureAwait(false) : raw;
        }
        catch (Exception ex)
        {
            // VAD/STT can throw on a failed first-use model download (Whisper + Silero are fetched lazily,
            // ~1.6 GB) or a native transcription fault. The caller (SessionPanelViewModel.EndVoiceHoldAsync)
            // catches this to show a "Voice error" status, but without a log line the failure was invisible —
            // exactly why F9 looked like a dead hotkey. Log it here, then let the caller surface it.
            logger.LogError(ex, "Voice dictation failed after capture (VAD/STT/cleanup)");
            throw;
        }
    }

    private async Task<List<byte>> _CaptureAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        try
        {
            await foreach (var frame in captureService.CaptureAsync(CaptureFormat, cancellationToken).ConfigureAwait(false))
            {
                AudioLevelSampled?.Invoke(this, AudioLevelMeter.NormalizedRms(frame.Span));
                buffer.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: EndHoldAsync cancels the capture stream when the hotkey is released.
        }

        return buffer;
    }

    private static float[] _ToFloatSamples(List<byte> pcmS16Bytes)
    {
        var sampleCount = pcmS16Bytes.Count / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s16 = (short)(pcmS16Bytes[i * 2] | (pcmS16Bytes[(i * 2) + 1] << 8));
            samples[i] = s16 / (float)short.MaxValue;
        }

        return samples;
    }
}
