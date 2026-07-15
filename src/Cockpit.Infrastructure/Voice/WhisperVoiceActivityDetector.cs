using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="IVoiceActivityDetector"/> backed by Whisper.net's built-in Silero-VAD support
/// (<see cref="WhisperVadFactory"/>), which runs on the same native runtime already loaded for STT —
/// no separate ONNX Runtime dependency. Thresholds mirror WisperFlow's own <c>VadOptions</c> (research:
/// Cockpit-DotNet-Voice-Stack-2026-07-07.md §2): 250 ms min speech, 100 ms min silence, 30 ms padding.
/// Lazily initializes on first use, same reasoning as <see cref="WhisperSpeechToTextService"/>.
/// </summary>
internal sealed class WhisperVoiceActivityDetector(ILogger<WhisperVoiceActivityDetector> logger)
    : IVoiceActivityDetector, ISingletonService, IAsyncDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperVadFactory? _factory;
    private WhisperVadProcessor? _processor;

    public async Task<bool> HasSpeechAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0)
        {
            return false;
        }

        var processor = await _EnsureProcessorAsync(cancellationToken).ConfigureAwait(false);
        var segments = await processor.DetectSpeechAsync(samples, cancellationToken).ConfigureAwait(false);
        return segments.Count > 0;
    }

    private async Task<WhisperVadProcessor> _EnsureProcessorAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            return _processor;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processor is not null)
            {
                return _processor;
            }

            var modelPath = await WhisperModelCache.EnsureVadDownloadedAsync(SileroVadType.V6_2_0, cancellationToken, logger).ConfigureAwait(false);
            _factory = WhisperVadFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder()
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(30))
                .Build();

            logger.LogInformation("Silero VAD initialized");
            return _processor;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        _factory?.Dispose();
        _initLock.Dispose();
    }
}
