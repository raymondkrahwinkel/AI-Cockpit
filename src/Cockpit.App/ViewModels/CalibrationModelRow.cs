namespace Cockpit.App.ViewModels;

/// <summary>
/// One model's row in the calibration's model ladder (AC-68): the model name, its measured latency on the chosen
/// backend (raw for the bar, formatted for text), whether the verdict recommends it, and whether it is the model
/// currently configured. The bars read relative to the slowest model measured.
/// </summary>
public sealed record CalibrationModelRow(
    string Model,
    double LatencyMs,
    string SpeedText,
    bool IsRecommended,
    bool IsCurrent);
