using System.Text;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ISpeechToTextService"/> backed by Whisper.net. Everything — the runtime-order pick, the
/// model download, and the native <see cref="WhisperFactory"/>/<see cref="WhisperProcessor"/> — is
/// deferred to the first <see cref="TranscribeAsync"/> call: voice is opt-in (#voice), so a disabled
/// operator never pays for the model download or the native library load.
/// </summary>
internal sealed class WhisperSpeechToTextService(IVoiceSettingsStore settingsStore, ILogger<WhisperSpeechToTextService> logger)
    : ISpeechToTextService, ISingletonService, IAsyncDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperRuntimeBackend? ActiveBackend { get; private set; }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        var processor = await _EnsureProcessorAsync(cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    private async Task<WhisperProcessor> _EnsureProcessorAsync(CancellationToken cancellationToken)
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

            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var modelType = WhisperModelCatalog.Resolve(settings.ModelName);
            var modelPath = await WhisperModelCache.EnsureDownloadedAsync(modelType, cancellationToken).ConfigureAwait(false);

            var order = WhisperBackendPlanner.BuildOrder(settings.BackendPreference, OperatingSystem.IsWindows());
            RuntimeOptions.RuntimeLibraryOrder = order.Select(WhisperRuntimeBackendMapping.ToNative).ToList();

            _factory = WhisperFactory.FromPath(modelPath);
            ActiveBackend = RuntimeOptions.LoadedLibrary is { } loaded ? WhisperRuntimeBackendMapping.FromNative(loaded) : null;
            logger.LogInformation(
                "Whisper STT initialized: model={Model}, backend={Backend}", modelType, ActiveBackend?.ToString() ?? "unknown");

            _processor = _factory.CreateBuilder().WithLanguage("auto").Build();
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
