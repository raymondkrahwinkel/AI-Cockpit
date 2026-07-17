using System.Diagnostics;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Runs the first-use calibration (AC-68) in two phases. Phase one times the configured model on every backend this
/// machine can use — the CPU always, plus the GPU when a runtime loads — each in its own child process (Whisper.net
/// loads its native runtime once per process), while this parent samples desktop hitch (GPU contention is
/// system-wide, so a child on the GPU stutters this desktop as a real dictation would). A CPU-preferring verdict
/// picks the backend. Phase two then times a ladder of models on that winning backend — one child, one factory per
/// model — so the accuracy-vs-speed advice rests on real numbers too. The whole result is remembered per machine.
/// </summary>
internal sealed class TranscriptionCalibrator(
    ITranscriptionAdvisor advisor,
    IUiHitchProbe hitchProbe,
    ITranscriptionCalibrationStore store,
    IVoiceSettingsStore settingsStore,
    ILogger<TranscriptionCalibrator> logger) : ITranscriptionCalibrator, ISingletonService
{
    /// <summary>The models the phase-two ladder spans, most to least accurate. The configured model is added if it
    /// is not already here, so the table always includes what the operator actually runs.</summary>
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

        // Phase two: a ladder of models on the winning backend, to advise which model to run.
        var ladder = await _MeasureModelLadderAsync(chosen, model, progress, cancellationToken).ConfigureAwait(false);
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

        // The hitch probe runs here, in the desktop process, for the whole of the child's run. A CPU measurement
        // should register no hitch; a GPU one registers whatever the desktop actually suffered while the GPU was busy.
        using var hitchSession = hitchProbe.Start();
        var results = await _RunChildAsync(backend, [model], label, progress, cancellationToken).ConfigureAwait(false);

        var result = results.FirstOrDefault();
        if (result?.LatencyMs is not { } latencyMs)
        {
            logger.LogWarning("Calibration of the {Label} backend produced no result", label);

            return null;
        }

        return new BackendMeasurement(result.Backend ?? backend, latencyMs, hitchSession.MaxHitchMs);
    }

    private async Task<IReadOnlyList<ModelMeasurement>> _MeasureModelLadderAsync(
        VoiceBackendPreference backend,
        string configuredModel,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var ladderModels = _ModelLadderFor(configuredModel);
        var results = await _RunChildAsync(backend, ladderModels, "Model ladder", progress, cancellationToken).ConfigureAwait(false);

        return results
            .Where(result => result.Model is not null && result.LatencyMs is not null)
            .Select(result => new ModelMeasurement(result.Model!, result.LatencyMs!.Value))
            .ToList();
    }

    private static IReadOnlyList<string> _ModelLadderFor(string configuredModel)
    {
        var ladder = new List<string>(CuratedModelLadder);
        if (!ladder.Any(model => string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)))
        {
            ladder.Add(configuredModel);
        }

        return ladder;
    }

    /// <summary>
    /// Spawns this same executable in its headless calibration mode for one backend and a set of models, relaying
    /// its protocol lines and collecting one result per model. A child that outlives a cancellation is killed with
    /// its tree so a wedged native load cannot linger.
    /// </summary>
    private async Task<IReadOnlyList<CalibrationChildMessage>> _RunChildAsync(
        VoiceBackendPreference backend,
        IReadOnlyList<string> models,
        string label,
        IProgress<CalibrationProgress>? progress,
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
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line || CalibrationChildProtocol.Decode(line) is not { } message)
            {
                return;
            }

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

        return results;
    }
}
