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
/// <para>
/// The loaded model is large (large-v3-turbo is ~1.5 GB resident) and, once loaded, would otherwise sit in
/// memory for the rest of the app's life. So it is unloaded again after <see cref="IdleUnloadAfter"/> of no
/// dictation and transparently reloaded (from the on-disk model cache, no re-download) on the next call —
/// giving the memory back on a machine that is also running local LLMs, without slowing an active dictation
/// session (a clip is seconds; the idle floor is minutes).
/// </para>
/// </summary>
internal sealed class WhisperSpeechToTextService(IVoiceSettingsStore settingsStore, ILogger<WhisperSpeechToTextService> logger)
    : ISpeechToTextService, ISingletonService, IAsyncDisposable
{
    /// <summary>How long the model may sit unused before it is unloaded to free memory. Reloaded on the next dictation.</summary>
    private static readonly TimeSpan IdleUnloadAfter = TimeSpan.FromMinutes(5);

    /// <summary>How often the idle check runs — coarse on purpose, since the floor above is in minutes.</summary>
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _processorLanguage;
    private long _lastUsedTicks;
    private int _inFlight;
    private Timer? _idleTimer;
    private bool _disposed;

    public WhisperRuntimeBackend? ActiveBackend { get; private set; }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        // Marks the model in-use for the idle unloader: the count keeps it from being disposed mid-transcription,
        // the timestamp starts the idle clock from the end of the last call.
        Interlocked.Increment(ref _inFlight);
        try
        {
            Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            var processor = await _EnsureProcessorAsync(cancellationToken).ConfigureAwait(false);

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            return text.ToString().Trim();
        }
        finally
        {
            Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<WhisperProcessor> _EnsureProcessorAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var language = string.IsNullOrWhiteSpace(settings.SttLanguage) ? "auto" : settings.SttLanguage;

        // The model/factory load is one-time (until an idle unload); only the processor is rebuilt when the
        // operator changes the dictation language in Options, so switching language takes effect without a restart.
        if (_processor is not null && _processorLanguage == language)
        {
            return _processor;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processor is not null && _processorLanguage == language)
            {
                return _processor;
            }

            if (_factory is null)
            {
                var modelType = WhisperModelCatalog.Resolve(settings.ModelName);
                var modelPath = await WhisperModelCache.EnsureDownloadedAsync(modelType, cancellationToken, logger).ConfigureAwait(false);

                // Everything below has to happen before the first factory exists: RuntimeOptions is read once,
                // when the natives are loaded, and ggml reads its shader path from the environment at the same
                // moment. Get either one late and the loader quietly settles for the CPU.
                var platform = WhisperRuntimeCache.CurrentPlatform;
                var order = platform is { } host
                    ? WhisperBackendPlanner.BuildOrder(settings.BackendPreference, host)
                    : [WhisperRuntimeBackend.Cpu];
                RuntimeOptions.RuntimeLibraryOrder = order.Select(WhisperRuntimeBackendMapping.ToNative).ToList();

                if (platform is { } fetchHost)
                {
                    // The GPU runtimes are fetched on first use instead of bundled — only the one this machine
                    // can actually use, and only if it is not cached already.
                    await WhisperRuntimeCache.EnsureAvailableAsync(order, fetchHost, cancellationToken, logger).ConfigureAwait(false);
                    RuntimeOptions.LibraryPath = WhisperRuntimeCache.SearchPath;
                }

                // macOS only: Metal comes with the bundled CPU runtime, but its shader has to be findable.
                WhisperMetalShader.EnsureDiscoverable(logger);

                _factory = WhisperFactory.FromPath(modelPath);
                ActiveBackend = RuntimeOptions.LoadedLibrary is { } loaded ? WhisperRuntimeBackendMapping.FromNative(loaded) : null;
                logger.LogInformation(
                    "Whisper STT initialized: model={Model}, backend={Backend}", modelType, ActiveBackend?.ToString() ?? "unknown");

                // Start the idle unloader once the model actually exists; it is a no-op until then.
                _idleTimer ??= new Timer(_ => _ = _UnloadIfIdleAsync(), null, IdleCheckInterval, IdleCheckInterval);
            }

            var previous = _processor;
            _processor = _factory.CreateBuilder().WithLanguage(language).Build();
            _processorLanguage = language;
            logger.LogInformation("Whisper STT language set to {Language}", language);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return _processor;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Unloads the model when it has gone unused for <see cref="IdleUnloadAfter"/> and nothing is transcribing
    /// right now — freeing the ~1.5 GB it holds. The next <see cref="TranscribeAsync"/> rebuilds it from the
    /// on-disk cache. Both checks are repeated under the init lock so it can never race a reload.
    /// </summary>
    private async Task _UnloadIfIdleAsync()
    {
        if (_factory is null || !_IsIdle())
        {
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_factory is null || Volatile.Read(ref _inFlight) > 0 || !_IsIdle())
            {
                return;
            }

            var processor = _processor;
            var factory = _factory;
            _processor = null;
            _processorLanguage = null;
            _factory = null;
            ActiveBackend = null;

            if (processor is not null)
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }

            factory.Dispose();
            logger.LogInformation(
                "Whisper STT idle for {Minutes} min; model unloaded to free memory (reloads on next dictation)",
                (int)IdleUnloadAfter.TotalMinutes);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private bool _IsIdle() =>
        DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc) >= IdleUnloadAfter;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        _factory?.Dispose();
        _initLock.Dispose();
    }
}
