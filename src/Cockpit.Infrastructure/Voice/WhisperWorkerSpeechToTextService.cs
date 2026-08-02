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

    // Matches the wire format DictationWorkerProtocol documents (16 kHz mono float32) — used only to turn a
    // sample count back into a duration for the AC-535 trace, not to reinterpret the samples themselves.
    private const int SampleRateHz = 16_000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Spawning has its own gate rather than borrowing _gate (AC-603). _gate serializes clips and is taken by
    // TranscribeAsync alone; WarmUpAsync reaches the spawn from the hotkey press without it, so the invariant
    // "one spawn at a time" has to live with the spawn instead of with one of its two callers.
    private readonly SemaphoreSlim _spawnGate = new(1, 1);

    private readonly object _stateLock = new();
    private Process? _worker;
    private Stream? _stdin;
    private TaskCompletionSource? _ready;
    private TaskCompletionSource<string>? _pending;
    private bool _forceCpu;
    private long _lastUsedTicks;
    private Timer? _idleTimer;
    private bool _disposed;
    private VoiceBackendPreference _lastBackend;

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
            var recordingSeconds = samples.Length / (double)SampleRateHz;
            var stopwatch = Stopwatch.StartNew();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // Timed on its own: a cold start pays for the model coming down and the runtime activating,
                    // and that is the single most expensive step in a dictation. Folded into one total it is
                    // invisible — "3.8 s" says nothing about whether the machine is slow or the worker was cold.
                    var startupStopwatch = Stopwatch.StartNew();
                    var coldStart = await _EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
                    startupStopwatch.Stop();

                    var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_stateLock)
                    {
                        _pending = pending;
                    }

                    // Whatever was being prepared is done and the samples are going in now.
                    Prepared?.Invoke(this, EventArgs.Empty);
                    await _WriteClipAsync(samples, cancellationToken).ConfigureAwait(false);

                    await using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
                    var text = await pending.Task.ConfigureAwait(false);

                    _LogTranscribed(recordingSeconds, stopwatch.ElapsedMilliseconds, startupStopwatch.ElapsedMilliseconds, coldStart, text.Length);

                    return text;
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

    /// <summary>
    /// Spawns the worker ahead of the clip that is coming (AC-603). Swallows its failure on purpose: nobody is
    /// waiting on this call, and the transcription that follows reports the same problem where it can be seen.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        // The press counts as use, or the idle reaper still measures from the last clip: a worker warmed at
        // 12:06 after a dictation at 12:00 is six minutes "idle" the moment it comes up, and the next tick
        // kills it — quite possibly between this press and its release.
        Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        try
        {
            await _EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Warming the transcription worker failed; the next dictation will try again.");
        }
    }

    /// <summary>Ensures a live worker exists for the coming clip, returning whether this call had to spawn one
    /// (a cold start, AC-535) rather than reuse an already-warm process.</summary>
    /// <remarks>
    /// Serialized on <c>_spawnGate</c> because it has two callers that hold nothing in common (AC-603): the clip's
    /// own path under <c>_gate</c>, and <see cref="WarmUpAsync"/> from the hotkey press under nothing at all. Let
    /// both past the health check below and the second one kills the process the first is still waiting on and
    /// starts the cold start over — at the release, which is the exact wait warming up exists to remove. Holding
    /// the gate across the spawn is the point: a release arriving mid-spawn waits for that worker rather than
    /// replacing it.
    /// </remarks>
    private async Task<bool> _EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        await _spawnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _SpawnIfNeededAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _spawnGate.Release();
        }
    }

    private async Task<bool> _SpawnIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false } && _stdin is not null && _ready is { Task.IsCompletedSuccessfully: true })
        {
            return false;
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

        // Remembered, never logged directly (AC-534): whisper.cpp/CUDA stderr chatter is routine, and echoing every
        // line at any level would repeat AC-533's mistake of one noisy source drowning out the rest of the log. The
        // tail only surfaces once the worker actually fails, folded into the exception the caller already logs.
        var stderrTail = new ProcessStderrTail();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => _OnWorkerLine(args.Data, stderrTail);
        process.ErrorDataReceived += (_, args) => stderrTail.OnLine(args.Data);
        process.Exited += (_, _) => _OnWorkerExited(stderrTail);

        lock (_stateLock)
        {
            _ready = ready;
            _worker = process;
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _stdin = process.StandardInput.BaseStream;
        _lastBackend = backend;
        _idleTimer ??= new Timer(_ => _KillIfIdle(), null, IdleCheckInterval, IdleCheckInterval);

        // Wait for the worker to activate the native runtime and load the model. Throws if it died first (the Exited
        // handler faults the tcs), which the retry above turns into a CPU fallback.
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void _OnWorkerLine(string? line, ProcessStderrTail stderrTail)
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
                var error = new InvalidOperationException(_WithStderrTail(message.Message ?? "dictation worker error", stderrTail));
                _ready?.TrySetException(error);
                _pending?.TrySetException(error);
                break;
        }
    }

    private void _OnWorkerExited(ProcessStderrTail stderrTail)
    {
        // Died before ready (a load abort) or mid-clip (an inference abort): fault whatever is waiting so the retry in
        // TranscribeAsync kicks in. The respawn itself happens there, under the gate — never from this callback.
        var error = new InvalidOperationException(_WithStderrTail("The dictation worker process exited unexpectedly.", stderrTail));
        _ready?.TrySetException(error);
        _pending?.TrySetException(error);
    }

    /// <summary>Folds the worker's remembered stderr tail into a failure message, so "exited unexpectedly" says why
    /// (AC-534) — or leaves the message alone if the worker said nothing on stderr before it died.</summary>
    private static string _WithStderrTail(string message, ProcessStderrTail stderrTail)
    {
        var tail = stderrTail.Snapshot();
        return tail.Length == 0 ? message : $"{message} Stderr tail:{Environment.NewLine}{tail}";
    }

    /// <summary>
    /// The dictation trace (AC-535): recording length, backend, transcription time and outcome length, as one line
    /// per successful clip. The startup time is reported separately rather than as a flag beside the total, because
    /// a cold start is the most expensive step there is and folding it into one number hides which of the two was
    /// slow. A failed clip is already logged (Warning on the CPU retry, Error if that fails too) — this only covers
    /// the path that had nothing to say about itself before.
    /// </summary>
    private void _LogTranscribed(double recordingSeconds, long elapsedMs, long startupMs, bool coldStart, int textLength) =>
        logger.LogInformation(
            "Dictation transcribed {RecordingSeconds:F1}s of audio on {Backend} in {ElapsedMs} ms " +
            "({StartupMs} ms of that starting the worker, cold start: {ColdStart}); {Length} chars.",
            recordingSeconds, _lastBackend, elapsedMs, startupMs, coldStart, textLength);

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
        bool hasWorker;
        bool busy;
        lock (_stateLock)
        {
            hasWorker = _worker is not null;
            busy = _pending is not null;
        }

        // Nothing to do once a prior tick already killed the worker: without this, every later tick re-read the
        // same stale _lastUsedTicks and fired the log again — every minute, forever (AC-533).
        if (!hasWorker || busy)
        {
            return;
        }

        var idleFor = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc);
        if (idleFor < IdleUnloadAfter)
        {
            return;
        }

        _KillWorker();
        logger.LogInformation("Dictation worker idle for {Minutes:F1} min; killed to free memory (respawns on next dictation).", idleFor.TotalMinutes);
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
        _spawnGate.Dispose();
    }
}
