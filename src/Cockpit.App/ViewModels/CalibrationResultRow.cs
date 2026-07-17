namespace Cockpit.App.ViewModels;

/// <summary>
/// One backend's row in the calibration results (AC-68): its label, the measured latency and desktop hitch (both
/// as a raw value for the comparison bars and as formatted text), whether the verdict chose it, and whether it kept
/// the desktop smooth. The bars read relative to the slowest backend, so a fast GPU next to a slow CPU is obvious.
/// </summary>
public sealed record CalibrationResultRow(
    string Label,
    double LatencyMs,
    double HitchMs,
    string SpeedText,
    string HitchText,
    bool IsChosen,
    bool IsSmooth);
