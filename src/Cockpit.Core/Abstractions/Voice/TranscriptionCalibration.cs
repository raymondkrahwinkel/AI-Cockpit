using System.Globalization;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>One backend's measured result (AC-68): how long the calibration clip took on it, and how much the
/// desktop hitched while it ran. Latency is measured in an isolated child process (one native per process, so
/// each backend needs its own), the hitch in the desktop process that was running alongside it.</summary>
public sealed record BackendMeasurement(VoiceBackendPreference Backend, double LatencyMs, double HitchMs);

/// <summary>One model's measured latency on the chosen backend (AC-68): the model-ladder timing that backs the
/// accuracy-vs-speed advice. The native runtime is loaded once per process, but the model is not — so a single
/// child on the chosen backend can time the whole ladder by rebuilding the factory per model.</summary>
public sealed record ModelMeasurement(string Model, double LatencyMs);

/// <summary>
/// A measured first-use calibration (AC-68): the per-backend results and the backend the verdict chose from them,
/// plus a per-model latency ladder on that backend and the model it recommends. Where slice 3 measured only the one
/// backend that happened to be loaded, this measures every backend the machine can use — each in its own process —
/// and then times a spread of models on the winner, so both the backend and the model advice rest on real numbers.
/// </summary>
public sealed record TranscriptionCalibration(
    IReadOnlyList<BackendMeasurement> Measurements,
    VoiceBackendPreference ChosenBackend,
    IReadOnlyList<ModelMeasurement> ModelLadder,
    string RecommendedModel,
    string Model);

/// <summary>A step in a running calibration (AC-68): the line to show and, when a download can report one, a 0..1
/// fraction for a bar. Null fraction means the step has no honest percentage (loading, warming up, measuring).</summary>
public sealed record CalibrationProgress(string Message, double? Fraction = null);

/// <summary>Persists the calibration <b>per machine</b> (AC-68): a config can be synced or restored onto a
/// different box, and a measurement from one machine's GPU says nothing about another's.</summary>
public interface ITranscriptionCalibrationStore
{
    /// <summary>The calibration measured on this machine, or null if it has never been run here.</summary>
    Task<TranscriptionCalibration?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TranscriptionCalibration calibration, CancellationToken cancellationToken = default);
}

/// <summary>Runs the actual measurement (AC-68): times the calibration clip on every backend this machine can use —
/// each in an isolated child process — while sampling desktop hitch, then decides which backend to use and
/// remembers the result for this machine.</summary>
public interface ITranscriptionCalibrator
{
    Task<TranscriptionCalibration> MeasureAsync(IProgress<CalibrationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>A session that samples UI-thread scheduling jitter — a cheap proxy for how much the desktop stutters —
/// from <see cref="Start"/> until disposed (AC-68). Implemented in the UI layer where the dispatcher lives.</summary>
public interface IUiHitchProbe
{
    IUiHitchSession Start();
}

/// <summary>The worst UI-thread stall seen since the probe started, in milliseconds.</summary>
public interface IUiHitchSession : IDisposable
{
    double MaxHitchMs { get; }
}

/// <summary>
/// Turns the per-backend measurements into a verdict and words (AC-68). Pure, so the "prefer the CPU unless it is
/// meaningfully slower" reasoning is unit-testable without running a real transcription.
/// <para>
/// The governing rule is <em>CPU preference, decided on measurements</em>: the CPU keeps the desktop perfectly
/// smooth, so it wins as long as it is not much slower than the GPU. "Much" is <see cref="CpuPreferenceFactor"/>
/// when the GPU stays smooth, and the more forgiving <see cref="CpuPreferenceFactorWhenGpuHitches"/> when the GPU
/// hitches the desktop — a GPU that stutters has to be that much faster still to be worth leaving the CPU for.
/// </para>
/// </summary>
public static class TranscriptionCalibrationReport
{
    /// <summary>A stall at or under roughly one 60 Hz frame reads as smooth; beyond it the desktop visibly hitches.</summary>
    public const double SmoothHitchMs = 16.0;

    /// <summary>The CPU is preferred while it is at most this many times slower than the GPU and the GPU is smooth.</summary>
    public const double CpuPreferenceFactor = 1.5;

    /// <summary>A wider margin used when the GPU hitches the desktop: the CPU is preferred until it is this many
    /// times slower, because the GPU's speed then comes at a visible cost.</summary>
    public const double CpuPreferenceFactorWhenGpuHitches = 3.0;

    public static bool IsSmooth(BackendMeasurement measurement) => measurement.HitchMs <= SmoothHitchMs;

    public static bool IsGpu(VoiceBackendPreference backend) =>
        backend is VoiceBackendPreference.Cuda or VoiceBackendPreference.Vulkan;

    /// <summary>
    /// Picks the backend to use from the measurements and explains why. CPU-preferred: the CPU wins unless it is
    /// meaningfully slower than the fastest measured GPU (see the type remarks for the thresholds).
    /// </summary>
    public static (VoiceBackendPreference Backend, string Rationale) Decide(IReadOnlyList<BackendMeasurement> measurements)
    {
        var cpu = measurements.FirstOrDefault(m => m.Backend is VoiceBackendPreference.Cpu);
        var gpu = measurements.Where(m => IsGpu(m.Backend)).OrderBy(m => m.LatencyMs).FirstOrDefault();

        if (cpu is null && gpu is null)
        {
            return (VoiceBackendPreference.Cpu, "No backend could be measured on this machine.");
        }

        if (gpu is null)
        {
            return (VoiceBackendPreference.Cpu, $"Only the CPU could be measured — a sentence took {_Sec(cpu!.LatencyMs)}s.");
        }

        if (cpu is null)
        {
            return (gpu.Backend, $"Only the GPU could be measured — a sentence took {_Sec(gpu.LatencyMs)}s.");
        }

        var gpuSmooth = IsSmooth(gpu);
        var factor = gpuSmooth ? CpuPreferenceFactor : CpuPreferenceFactorWhenGpuHitches;
        if (cpu.LatencyMs <= gpu.LatencyMs * factor)
        {
            var reason = gpuSmooth
                ? $"On the CPU a sentence took {_Sec(cpu.LatencyMs)}s versus the GPU's {_Sec(gpu.LatencyMs)}s — close enough that the CPU is worth it to keep the desktop perfectly smooth."
                : $"On the CPU a sentence took {_Sec(cpu.LatencyMs)}s versus the GPU's {_Sec(gpu.LatencyMs)}s, and the GPU hitched the desktop {_Ms(gpu.HitchMs)}ms — the CPU stays smooth for a small speed cost.";

            return (VoiceBackendPreference.Cpu, reason);
        }

        var hitchNote = gpuSmooth
            ? $"and the desktop stayed smooth ({_Ms(gpu.HitchMs)}ms)"
            : $"at the cost of a {_Ms(gpu.HitchMs)}ms desktop hitch";

        return (gpu.Backend, $"On the GPU a sentence took {_Sec(gpu.LatencyMs)}s versus the CPU's {_Sec(cpu.LatencyMs)}s — much faster, {hitchNote}.");
    }

    /// <summary>The verdict's words for an already-measured calibration.</summary>
    public static string Rationale(TranscriptionCalibration calibration) => Decide(calibration.Measurements).Rationale;

    /// <summary>A sentence should transcribe within this on the chosen backend to feel responsive for dictation.</summary>
    public const double ResponsiveModelBudgetMs = 3000.0;

    /// <summary>Curated models from least to most accurate. Drives the "most accurate model that is still
    /// responsive" pick; a name not on the list ranks lowest so an unmeasurable custom name can never be
    /// recommended over a real measured model.</summary>
    private static readonly string[] ModelAccuracyOrder =
    [
        "tiny", "tiny.en", "base", "base.en", "small", "small.en",
        "medium", "medium.en", "large-v1", "large-v2", "large-v3", "large-v3-turbo",
    ];

    public static int ModelAccuracyRank(string model) =>
        Array.FindIndex(ModelAccuracyOrder, name => string.Equals(name, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Picks the model to advise from the ladder measured on the chosen backend: the most accurate model that still
    /// transcribes within <see cref="ResponsiveModelBudgetMs"/>. If nothing is responsive (a slow CPU-only box), the
    /// fastest is advised instead — the operator can still pick a bigger one from the table, knowing the cost.
    /// </summary>
    public static (string Model, string Rationale) RecommendModel(IReadOnlyList<ModelMeasurement> ladder)
    {
        if (ladder.Count == 0)
        {
            return (string.Empty, "No model could be measured.");
        }

        var responsive = ladder.Where(measurement => measurement.LatencyMs <= ResponsiveModelBudgetMs).ToList();
        if (responsive.Count > 0)
        {
            var best = responsive.OrderByDescending(measurement => ModelAccuracyRank(measurement.Model)).First();

            return (best.Model, $"{best.Model} is the most accurate model that still transcribes a sentence within {_Sec(ResponsiveModelBudgetMs)}s here ({_Sec(best.LatencyMs)}s).");
        }

        var fastest = ladder.OrderBy(measurement => measurement.LatencyMs).First();

        return (fastest.Model, $"Every model is slow on this backend; {fastest.Model} is the fastest at {_Sec(fastest.LatencyMs)}s — a smaller model trades accuracy for responsiveness.");
    }

    private static string _Sec(double milliseconds) => (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture);

    private static string _Ms(double milliseconds) => milliseconds.ToString("0", CultureInfo.InvariantCulture);
}
