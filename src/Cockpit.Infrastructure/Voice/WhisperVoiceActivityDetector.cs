using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice;

// AC-1013: `IVoiceActivityDetector` via Whisper.net's Silero-VAD, sharing the one native runtime; it is what
// actually *loads* that runtime first (before the STT service), hence `WhisperRuntimeProvisioner` must run first.
// Trimmed: VadOptions threshold source, the in-process native-crash caveat (unlike isolated STT, AC-174).
internal sealed class WhisperVoiceActivityDetector(
    WhisperRuntimeProvisioner runtimeProvisioner, ILogger<WhisperVoiceActivityDetector> logger)
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

            // Before the factory, never after: this load is what fixes the backend for the process, and the
            // options only count until it happens.
            await runtimeProvisioner.EnsurePreparedAsync(cancellationToken).ConfigureAwait(false);
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
