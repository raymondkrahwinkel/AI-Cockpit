using System.Diagnostics;
using System.Text.Json;
using Cockpit.Core.Voice;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// The line protocol between the calibration orchestrator (the desktop process) and a measurement child (AC-68).
/// Whisper.net loads its native runtime once per process, so each backend has to be timed in its own process; the
/// orchestrator spawns this exe with <see cref="BackendArgument"/>/<see cref="ModelsArgument"/> and reads these
/// prefixed JSON lines back off stdout. The prefix keeps them apart from the native loader's own chatter. A child
/// times one backend and, since the model is not pinned to the process the way the native is, every model in the
/// comma-separated list on that one backend — one factory build per model.
/// </summary>
internal static class CalibrationChildProtocol
{
    public const string BackendArgument = "--calibrate-backend";
    public const string ModelsArgument = "--calibrate-models";
    public const string LinePrefix = "CALIB ";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Encode(CalibrationChildMessage message) => LinePrefix + JsonSerializer.Serialize(message, Json);

    public static CalibrationChildMessage? Decode(string line) =>
        line.StartsWith(LinePrefix, StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<CalibrationChildMessage>(line[LinePrefix.Length..], Json)
            : null;

    /// <summary>Pulls the forced backend and the models to time out of the process arguments, or false when this is
    /// not a calibration child.</summary>
    public static bool TryReadRequest(string[] args, out VoiceBackendPreference backend, out string[] models)
    {
        backend = VoiceBackendPreference.Cpu;
        models = ["large-v3-turbo"];
        var index = Array.IndexOf(args, BackendArgument);
        if (index < 0 || index + 1 >= args.Length || !Enum.TryParse(args[index + 1], out backend))
        {
            return false;
        }

        var modelsIndex = Array.IndexOf(args, ModelsArgument);
        if (modelsIndex >= 0 && modelsIndex + 1 < args.Length)
        {
            var parsed = args[modelsIndex + 1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parsed.Length > 0)
            {
                models = parsed;
            }
        }

        return true;
    }
}

/// <summary>
/// The public seam the app's entrypoint uses to run a calibration child (AC-68). Kept tiny and public so
/// <c>Program.Main</c> can branch into headless measurement before it touches the single-instance guard, Avalonia,
/// or the DI container — none of which a one-shot measurement should pay for or contend with.
/// </summary>
public static class HeadlessCalibration
{
    /// <summary>Whether these process arguments ask for a headless backend measurement rather than a normal launch.</summary>
    public static bool IsRequested(string[] args) => CalibrationChildProtocol.TryReadRequest(args, out _, out _);

    /// <summary>Runs the measurement for the backend/models named in the arguments and returns the process exit code.</summary>
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        CalibrationChildProtocol.TryReadRequest(args, out var backend, out var models);

        return TranscriptionCalibrationProbe.RunAsync(backend, models, cancellationToken);
    }
}

/// <summary>One line from a measurement child: a progress step, a per-model result, or an error.</summary>
internal sealed record CalibrationChildMessage(
    string Kind,
    string? Message = null,
    double? Fraction = null,
    VoiceBackendPreference? Backend = null,
    string? Model = null,
    double? LatencyMs = null)
{
    public const string KindProgress = "progress";
    public const string KindResult = "result";
    public const string KindError = "error";
}

/// <summary>
/// The measurement half of the calibration, run headless in a child process (AC-68). It forces one backend onto
/// Whisper.net, then for each requested model provisions and loads it, warms up, and times a few transcriptions of
/// a synthetic clip — printing a per-model median latency back to the orchestrator. Desktop hitch is <em>not</em>
/// measured here — the child has no desktop; the parent samples that while this runs, since GPU contention is
/// system-wide.
/// </summary>
internal static class TranscriptionCalibrationProbe
{
    private const int SampleRate = 16000;
    private const int ClipSeconds = 4;
    private const int MeasuredRuns = 3;

    /// <summary>Runs the forced-backend measurement over every model and prints protocol lines to stdout.</summary>
    public static async Task<int> RunAsync(VoiceBackendPreference backend, string[] models, CancellationToken cancellationToken)
    {
        try
        {
            var host = WhisperRuntimeCache.CurrentPlatform
                       ?? throw new PlatformNotSupportedException("Whisper.net publishes no runtime for this OS.");

            var runtimeProgress = new Progress<VoicePreparationProgress>(step => _Emit(new(
                CalibrationChildMessage.KindProgress, step.Description, step.Fraction)));

            // The native runtime loads once for this process; every model rides on it.
            var order = WhisperBackendPlanner.BuildOrder(backend, host);
            await WhisperRuntimeActivation.ApplyAsync(order, host, cancellationToken, logger: null, runtimeProgress).ConfigureAwait(false);

            WhisperRuntimeBackend? loadedBackend = null;
            var clip = _CalibrationClip();

            foreach (var model in models)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modelType = WhisperModelCatalog.Resolve(model);
                var modelPath = await WhisperModelCache
                    .EnsureDownloadedAsync(modelType, cancellationToken, logger: null, runtimeProgress)
                    .ConfigureAwait(false);

                _Emit(new(CalibrationChildMessage.KindProgress, $"Loading {model}…"));
                using var factory = WhisperFactory.FromPath(modelPath);
                loadedBackend ??= RuntimeOptions.LoadedLibrary is { } native ? WhisperRuntimeBackendMapping.FromNative(native) : null;
                await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();

                // Warm up (untimed): the first pass primes caches and any lazy native init, so the measured runs
                // time steady-state transcription rather than one-off setup.
                _Emit(new(CalibrationChildMessage.KindProgress, $"Warming up {model}…"));
                await _DrainAsync(processor, clip, cancellationToken).ConfigureAwait(false);

                var latencies = new List<double>(MeasuredRuns);
                for (var run = 1; run <= MeasuredRuns; run++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _Emit(new(CalibrationChildMessage.KindProgress, $"Measuring {model}… ({run}/{MeasuredRuns})"));
                    var stopwatch = Stopwatch.StartNew();
                    await _DrainAsync(processor, clip, cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                latencies.Sort();
                _Emit(new(
                    CalibrationChildMessage.KindResult,
                    Backend: loadedBackend is { } actual ? _ToPreference(actual) : backend,
                    Model: model,
                    LatencyMs: latencies[latencies.Count / 2]));
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception exception)
        {
            _Emit(new(CalibrationChildMessage.KindError, exception.Message));

            return 1;
        }
    }

    private static async Task _DrainAsync(WhisperProcessor processor, float[] clip, CancellationToken cancellationToken)
    {
        await foreach (var _ in processor.ProcessAsync(clip, cancellationToken).ConfigureAwait(false))
        {
            // The transcription's text is irrelevant to a calibration; only the time it takes matters.
        }
    }

    private static void _Emit(CalibrationChildMessage message)
    {
        Console.Out.WriteLine(CalibrationChildProtocol.Encode(message));
        Console.Out.Flush();
    }

    // The native runtime that actually loaded, mapped to the user-facing three-way (the GPU families collapse to
    // Cuda/Vulkan; the two CPU builds to Cpu).
    private static VoiceBackendPreference _ToPreference(WhisperRuntimeBackend backend) => backend switch
    {
        WhisperRuntimeBackend.Cuda or WhisperRuntimeBackend.Cuda12 => VoiceBackendPreference.Cuda,
        WhisperRuntimeBackend.Vulkan => VoiceBackendPreference.Vulkan,
        _ => VoiceBackendPreference.Cpu,
    };

    // A few gliding tones under a syllable-rate amplitude envelope: enough to make the encoder and decoder do real
    // work (so the timing is representative), without bundling an audio asset. 16 kHz mono float32, the STT input.
    private static float[] _CalibrationClip()
    {
        var samples = new float[SampleRate * ClipSeconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * (t * 4 % 1)));
            var tone = Math.Sin(2 * Math.PI * 140 * t)
                       + 0.5 * Math.Sin(2 * Math.PI * 400 * t)
                       + 0.3 * Math.Sin(2 * Math.PI * 900 * t);
            samples[i] = (float)(0.2 * envelope * tone);
        }

        return samples;
    }
}
