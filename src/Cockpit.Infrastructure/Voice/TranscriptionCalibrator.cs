using System.Diagnostics;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Runs the first-use calibration (AC-68) in two phases. Phase one times the configured model on every backend this
/// machine can use — the CPU always, plus the GPU when a runtime loads — each in its own child process (Whisper.net
/// loads its native runtime once per process), while this parent samples desktop hitch across the timed runs (GPU
/// contention is system-wide, so a child on the GPU stutters this desktop as a real dictation would). A CPU-preferring
/// verdict picks the backend. Phase two then times a ladder of models on that winning backend — one child, one
/// factory per model — so the accuracy-vs-speed advice rests on real numbers too. The result is remembered per machine.
/// </summary>
internal sealed class TranscriptionCalibrator(
    ITranscriptionAdvisor advisor,
    IUiHitchProbe hitchProbe,
    ITranscriptionCalibrationStore store,
    IVoiceSettingsStore settingsStore,
    ILogger<TranscriptionCalibrator> logger) : ITranscriptionCalibrator, ISingletonService
{
    /// <summary>The models the phase-two ladder spans, most to least accurate. The configured model is added if it
    /// is a known model not already here, so the table includes what the operator actually runs.</summary>
    private static readonly string[] CuratedModelLadder = ["large-v3-turbo", "small", "base", "tiny"];

    public async Task<TranscriptionCalibration> MeasureAsync(
        IProgress<CalibrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var model = string.IsNullOrWhiteSpace(settings.ModelName) ? "large-v3-turbo" : settings.ModelName.Trim();

        // Phase one: the configured model on every usable backend, to choose the backend.
        var measurements = new List<BackendMeasurement>();
        foreach (var (backend, label) in _BackendsToMeasure())
        {
            var measurement = await _MeasureBackendAsync(backend, label, model, progress, cancellationToken).ConfigureAwait(false);
            if (measurement is not null)
            {
                measurements.Add(measurement);
            }
        }

        if (measurements.Count == 0)
        {
            throw new InvalidOperationException("No backend could be measured on this machine.");
        }

        var (chosen, rationale) = TranscriptionCalibrationReport.Decide(measurements);
        logger.LogInformation("Calibration measured {Count} backend(s); Auto will use {Backend} — {Rationale}", measurements.Count, chosen, rationale);

        // Phase two: a ladder of models on the winning backend, to advise which model to run. The configured model's
        // time on that backend is already known from phase one, so it is reused rather than measured again.
        var chosenLatencyMs = measurements.First(measurement => measurement.Backend == chosen).LatencyMs;
        var ladder = await _MeasureModelLadderAsync(chosen, model, chosenLatencyMs, progress, cancellationToken).ConfigureAwait(false);
        var recommendedModel = ladder.Count > 0 ? TranscriptionCalibrationReport.RecommendModel(ladder).Model : model;
        logger.LogInformation("Calibration model advice on {Backend}: {Model}", chosen, recommendedModel);

        var calibration = new TranscriptionCalibration(measurements, chosen, ladder, recommendedModel, model);
        await store.SaveAsync(calibration, cancellationToken).ConfigureAwait(false);

        return calibration;
    }

    /// <summary>The CPU always, plus the fastest-family GPU the probe says will actually load here.</summary>
    private IReadOnlyList<(VoiceBackendPreference Backend, string Label)> _BackendsToMeasure()
    {
        var backends = new List<(VoiceBackendPreference, string)> { (VoiceBackendPreference.Cpu, "CPU") };

        var capabilities = advisor.DetectCapabilities();
        if (capabilities.GpuUsable)
        {
            backends.Add((capabilities.CudaUsable ? VoiceBackendPreference.Cuda : VoiceBackendPreference.Vulkan, "GPU"));
        }

        return backends;
    }

    private async Task<BackendMeasurement?> _MeasureBackendAsync(
        VoiceBackendPreference backend,
        string label,
        string model,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new CalibrationProgress($"{label}: preparing…"));

        // Sample desktop hitch only across the timed runs, not the one-off runtime/model download and load: that
        // setup's disk and memory I/O would otherwise be blamed on the backend and could flip a smooth GPU to
        // "hitching", steering Auto onto the CPU for a stutter the real dictation never causes. The child announces
        // the timed runs with a "Measuring …" line, so start the probe when the first one arrives.
        IUiHitchSession? hitchSession = null;
        var results = await _RunChildAsync(
            backend,
            [model],
            label,
            progress,
            message =>
            {
                if (hitchSession is null && message.Kind == CalibrationChildMessage.KindProgress
                    && message.Message?.StartsWith("Measuring", StringComparison.Ordinal) == true)
                {
                    hitchSession = hitchProbe.Start();
                }
            },
            cancellationToken).ConfigureAwait(false);

        var hitchMs = hitchSession?.MaxHitchMs ?? 0;
        hitchSession?.Dispose();

        var result = results.FirstOrDefault();
        if (result?.LatencyMs is not { } latencyMs)
        {
            logger.LogWarning("Calibration of the {Label} backend produced no result", label);

            return null;
        }

        var actualBackend = result.Backend ?? backend;

        // A GPU child that quietly fell back to the CPU tail is not a GPU measurement. Drop it, so the table does not
        // show a phantom or duplicate CPU row and Decide is not handed a "GPU" that is really the CPU.
        if (TranscriptionCalibrationReport.IsGpu(backend) && !TranscriptionCalibrationReport.IsGpu(actualBackend))
        {
            logger.LogWarning("Calibration asked for the {Label} GPU but the child loaded {Actual}; the GPU is not usable here, dropping it", label, actualBackend);

            return null;
        }

        return new BackendMeasurement(actualBackend, latencyMs, hitchMs);
    }

    private async Task<IReadOnlyList<ModelMeasurement>> _MeasureModelLadderAsync(
        VoiceBackendPreference backend,
        string configuredModel,
        double configuredModelLatencyMs,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var configuredIsKnown = WhisperModelCatalog.IsKnown(configuredModel);

        // Only known models are timed — a custom/quantized name would resolve to the Base model in the child and be
        // shown under its own label. Skip the configured model here: phase one already timed it on this backend.
        var modelsToMeasure = _ModelLadderFor(configuredModel)
            .Where(model => !string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ladder = new List<ModelMeasurement>();
        if (modelsToMeasure.Count > 0)
        {
            var results = await _RunChildAsync(backend, modelsToMeasure, "Model ladder", progress, onMessage: null, cancellationToken).ConfigureAwait(false);
            ladder.AddRange(results
                .Where(result => result.Model is not null && result.LatencyMs is not null)
                .Select(result => new ModelMeasurement(result.Model!, result.LatencyMs!.Value)));
        }

        // Fold in the configured model's phase-one time on this backend, so the table shows what the operator runs
        // without paying to measure it twice — but only when it is a real model, not the Base fallback in disguise.
        if (configuredIsKnown)
        {
            ladder.Add(new ModelMeasurement(configuredModel, configuredModelLatencyMs));
        }

        return ladder;
    }

    private static IReadOnlyList<string> _ModelLadderFor(string configuredModel)
    {
        var ladder = new List<string>(CuratedModelLadder);
        if (WhisperModelCatalog.IsKnown(configuredModel)
            && !ladder.Any(model => string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)))
        {
            ladder.Add(configuredModel);
        }

        return ladder;
    }

    /// <summary>
    /// Spawns this same executable in its headless calibration mode for one backend and a set of models, relaying
    /// its protocol lines and collecting one result per model. A child that outlives a cancellation is killed with
    /// its tree so a wedged native load cannot linger; the caller's <paramref name="onMessage"/> sees every decoded
    /// line before it is handled here.
    /// </summary>
    private async Task<IReadOnlyList<CalibrationChildMessage>> _RunChildAsync(
        VoiceBackendPreference backend,
        IReadOnlyList<string> models,
        string label,
        IProgress<CalibrationProgress>? progress,
        Action<CalibrationChildMessage>? onMessage,
        CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The current process has no executable path to relaunch for calibration.");

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(CalibrationChildProtocol.BackendArgument);
        startInfo.ArgumentList.Add(backend.ToString());
        startInfo.ArgumentList.Add(CalibrationChildProtocol.ModelsArgument);
        startInfo.ArgumentList.Add(string.Join(',', models));

        var results = new List<CalibrationChildMessage>();
        var outputClosed = new TaskCompletionSource();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            // A null Data is the stdout EOF: the child has closed the pipe, so every line — including the final
            // result — has been delivered. Awaiting this below closes the race where WaitForExit returns first.
            if (args.Data is not { } line)
            {
                outputClosed.TrySetResult();

                return;
            }

            if (CalibrationChildProtocol.Decode(line) is not { } message)
            {
                return;
            }

            onMessage?.Invoke(message);
            switch (message.Kind)
            {
                case CalibrationChildMessage.KindProgress:
                    progress?.Report(new CalibrationProgress($"{label}: {message.Message}", message.Fraction));
                    break;
                case CalibrationChildMessage.KindResult:
                    results.Add(message);
                    break;
                case CalibrationChildMessage.KindError:
                    logger.LogWarning("Calibration child ({Label}) reported an error: {Error}", label, message.Message);
                    break;
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var kill = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The child raced us to exit; nothing to kill.
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await outputClosed.Task.ConfigureAwait(false);

        return results;
    }
}
