using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ISpeechToTextService"/> that runs Whisper in a child process (AC-174, Raymond 2026-07-22). Whisper.net loads
/// a native runtime that can <c>abort()</c> on a bad model or a GPU backend it cannot really use — a native crash no
/// managed handler can catch, which took the whole app down (a ggml_abort in whisper_model_load). Isolating it means a
/// native crash kills only the worker; the desktop respawns it and stays up. The worker is warm: spawned on first use, it
/// keeps the model loaded and takes clip after clip, and is killed after <see cref="IdleUnloadAfter"/> of no dictation to
/// give the ~1.5 GB back. If it crashes while loading — the classic GPU-backend abort — the next attempt is forced onto
/// the CPU backend, which does not abort, so dictation degrades to CPU instead of failing outright.
/// </summary>
internal sealed class WhisperWorkerSpeechToTextService(
    IVoiceSettingsStore settingsStore,
    ILogger<WhisperWorkerSpeechToTextService> logger)
    : ISpeechToTextService, ISingletonService, IAsyncDisposable
{
    private static readonly TimeSpan IdleUnloadAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private Process? _worker;
    private Stream? _stdin;
    private TaskCompletionSource? _ready;
    private TaskCompletionSource<string>? _pending;
    private bool _forceCpu;
    private long _lastUsedTicks;
    private Timer? _idleTimer;
    private bool _disposed;

    /// <summary>Not surfaced in worker mode — the loaded backend lives in the child process. Null is the documented "unknown".</summary>
    public WhisperRuntimeBackend? ActiveBackend => null;

    public event EventHandler<VoicePreparationProgress>? Preparing;
    public event EventHandler? Prepared;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        // One clip at a time: the worker's stdin/stdout is a single request/response channel.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

                    var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_stateLock)
                    {
                        _pending = pending;
                    }

                    // Whatever was being prepared is done and the samples are going in now.
                    Prepared?.Invoke(this, EventArgs.Empty);
                    await _WriteClipAsync(samples, cancellationToken).ConfigureAwait(false);

                    await using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
                    return await pending.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The clip was cancelled mid-inference, but the warm worker is still processing it and will emit its
                    // result against the next request — returning the previous clip's text. Kill it so that in-flight
                    // clip is discarded; the next dictation respawns a clean worker.
                    _KillWorker();
                    throw;
                }
                catch (Exception exception) when (attempt == 0 && !_forceCpu)
                {
                    // The worker died — almost always a native abort while loading a GPU backend. Retry once on CPU,
                    // which cannot abort that way, so dictation degrades rather than fails and the app stays up.
                    logger.LogWarning(exception, "Dictation worker failed; retrying on the CPU backend.");
                    _forceCpu = true;
                    _KillWorker();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Dictation worker failed on the CPU backend too; no text for this clip.");
                    _KillWorker();
                    return string.Empty;
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _pending = null;
            }

            Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            _gate.Release();
        }
    }

    private async Task _EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false } && _stdin is not null && _ready is { Task.IsCompletedSuccessfully: true })
        {
            return;
        }

        _KillWorker();

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var backend = _forceCpu ? VoiceBackendPreference.Cpu : settings.BackendPreference;
        var language = string.IsNullOrWhiteSpace(settings.SttLanguage) ? "auto" : settings.SttLanguage;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startInfo = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process has no executable path to relaunch for dictation."))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(DictationWorkerProtocol.TranscribeArgument);
        startInfo.ArgumentList.Add(DictationWorkerProtocol.BackendArgument);
        startInfo.ArgumentList.Add(backend.ToString());
        startInfo.ArgumentList.Add(DictationWorkerProtocol.ModelArgument);
        startInfo.ArgumentList.Add(settings.ModelName);
        startInfo.ArgumentList.Add(DictationWorkerProtocol.LanguageArgument);
        startInfo.ArgumentList.Add(language);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => _OnWorkerLine(args.Data);
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                logger.LogDebug("Dictation worker (stderr): {Line}", line);
            }
        };
        process.Exited += (_, _) => _OnWorkerExited();

        lock (_stateLock)
        {
            _ready = ready;
            _worker = process;
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _stdin = process.StandardInput.BaseStream;
        _idleTimer ??= new Timer(_ => _KillIfIdle(), null, IdleCheckInterval, IdleCheckInterval);

        // Wait for the worker to activate the native runtime and load the model. Throws if it died first (the Exited
        // handler faults the tcs), which the retry above turns into a CPU fallback.
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void _OnWorkerLine(string? line)
    {
        if (line is null || DictationWorkerProtocol.Decode(line) is not { } message)
        {
            return;
        }

        switch (message.Kind)
        {
            case DictationChildMessage.KindProgress:
                Preparing?.Invoke(this, new VoicePreparationProgress(message.Message ?? string.Empty, message.Fraction));
                break;
            case DictationChildMessage.KindReady:
                _ready?.TrySetResult();
                break;
            case DictationChildMessage.KindResult:
                _pending?.TrySetResult(message.Text ?? string.Empty);
                break;
            case DictationChildMessage.KindError:
                logger.LogWarning("Dictation worker reported an error: {Error}", message.Message);
                var error = new InvalidOperationException(message.Message ?? "dictation worker error");
                _ready?.TrySetException(error);
                _pending?.TrySetException(error);
                break;
        }
    }

    private void _OnWorkerExited()
    {
        // Died before ready (a load abort) or mid-clip (an inference abort): fault whatever is waiting so the retry in
        // TranscribeAsync kicks in. The respawn itself happens there, under the gate — never from this callback.
        var error = new InvalidOperationException("The dictation worker process exited unexpectedly.");
        _ready?.TrySetException(error);
        _pending?.TrySetException(error);
    }

    private async Task _WriteClipAsync(float[] samples, CancellationToken cancellationToken)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("The dictation worker has no input stream.");
        var buffer = new byte[sizeof(int) + (samples.Length * sizeof(float))];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(sizeof(int) + (i * sizeof(float))), BitConverter.SingleToInt32Bits(samples[i]));
        }

        await stdin.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void _KillIfIdle()
    {
        var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc) >= IdleUnloadAfter;
        bool busy;
        lock (_stateLock)
        {
            busy = _pending is not null;
        }

        if (idle && !busy)
        {
            _KillWorker();
            logger.LogInformation("Dictation worker idle for {Minutes} min; killed to free memory (respawns on next dictation).", (int)IdleUnloadAfter.TotalMinutes);
        }
    }

    private void _KillWorker()
    {
        Process? process;
        Stream? stdin;
        lock (_stateLock)
        {
            process = _worker;
            stdin = _stdin;
            _worker = null;
            _stdin = null;
            _ready = null;
        }

        if (stdin is not null)
        {
            try
            {
                stdin.Dispose();
            }
            catch (Exception)
            {
                // The worker already closed its end; nothing to flush.
            }
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Raced us to exit; nothing to kill.
        }
        finally
        {
            process.Dispose();
        }
    }

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

        _KillWorker();
        _gate.Dispose();
    }
}
