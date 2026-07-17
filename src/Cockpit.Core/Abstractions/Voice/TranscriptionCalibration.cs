using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// A measured first-use calibration (AC-68 slice 3): how long a transcription actually took on this machine, and
/// how much the desktop hitched while it ran, on the backend that was active. The recommendation (slice 2) is a
/// rule-table guess; this is the real number that confirms or overturns it — a GPU that measures a big desktop
/// hitch is one to move off, whatever the rules said.
/// </summary>
public sealed record TranscriptionCalibration(double LatencyMs, double HitchMs, VoiceBackendPreference Backend, string Model);

/// <summary>Persists the calibration <b>per machine</b> (AC-68 slice 3): a config can be synced or restored onto a
/// different box, and a measurement from one machine's GPU says nothing about another's.</summary>
public interface ITranscriptionCalibrationStore
{
    /// <summary>The calibration measured on this machine, or null if it has never been run here.</summary>
    Task<TranscriptionCalibration?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TranscriptionCalibration calibration, CancellationToken cancellationToken = default);
}

/// <summary>Runs the actual measurement (AC-68 slice 3): transcribes a fixed calibration clip on the configured
/// backend, timing it while sampling desktop hitch, and remembers the result for this machine.</summary>
public interface ITranscriptionCalibrator
{
    Task<TranscriptionCalibration> MeasureAsync(IProgress<string>? status = null, CancellationToken cancellationToken = default);
}

/// <summary>A session that samples UI-thread scheduling jitter — a cheap proxy for how much the desktop stutters —
/// from <see cref="Start"/> until disposed (AC-68 slice 3). Implemented in the UI layer where the dispatcher lives.</summary>
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
/// Turns a measurement into words and a verdict (AC-68 slice 3). Pure, so the "is the desktop smooth / should we
/// move off the GPU" reasoning is unit-testable without running a real transcription.
/// </summary>
public static class TranscriptionCalibrationReport
{
    /// <summary>A stall at or under roughly one 60 Hz frame reads as smooth; beyond it the desktop visibly hitches.</summary>
    public const double SmoothHitchMs = 16.0;

    public static bool IsDesktopSmooth(TranscriptionCalibration calibration) => calibration.HitchMs <= SmoothHitchMs;

    /// <summary>True when the measurement contradicts a GPU choice — it ran on the GPU but the desktop hitched —
    /// so the advice should steer to the CPU regardless of what the rule table guessed.</summary>
    public static bool SuggestsCpuInstead(TranscriptionCalibration calibration) =>
        calibration.Backend is VoiceBackendPreference.Cuda or VoiceBackendPreference.Vulkan && !IsDesktopSmooth(calibration);

    public static string Rationale(TranscriptionCalibration calibration)
    {
        var seconds = (calibration.LatencyMs / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var hitch = calibration.HitchMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        if (SuggestsCpuInstead(calibration))
        {
            return $"On the GPU a sentence took {seconds}s, but the desktop hitched {hitch}ms — enough to see. Switch to CPU to keep it smooth.";
        }

        if (calibration.Backend is VoiceBackendPreference.Cuda or VoiceBackendPreference.Vulkan)
        {
            return $"On the GPU a sentence took {seconds}s and the desktop stayed smooth ({hitch}ms).";
        }

        return $"On the CPU a sentence took {seconds}s and the desktop stayed smooth ({hitch}ms).";
    }
}
