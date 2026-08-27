namespace Cockpit.MeasurementHarness.Core;

/// <summary>Which part of a run a record was produced in. Verification never overlaps measurement (E3).</summary>
public enum Phase
{
    Setup,
    Measure,
    Verify,
}

/// <summary>Anything a run records, stamped with the phase it happened in.</summary>
public abstract record HarnessRecord(Phase Phase);

// E2: the three kinds are separate types, not three shapes of log line. A harness that counted its own
// phase marker reported `uifreeze hang = 1` with zero detector lines, and that fault was in two of the
// three harnesses at once (AC-1174, AC-1125).

/// <summary>Something the app's own diagnostics reported. Only these count towards a detector total.</summary>
public sealed record DetectorEvent(Phase Phase, string Detector, string Text) : HarnessRecord(Phase);

/// <summary>A note about what the run is doing. Never counted, however it is worded.</summary>
public sealed record PhaseMark(Phase Phase, string Text) : HarnessRecord(Phase);

/// <summary>One number the run produced.</summary>
public sealed record Measured(Phase Phase, string Name, double Value, string Unit) : HarnessRecord(Phase);

/// <summary>Collects a run's records and answers questions about them by type, never by text.</summary>
public sealed class Recorder
{
    private readonly List<HarnessRecord> _records = [];

    /// <summary>The phase new records are stamped with. Driven by the run, not by callers.</summary>
    public Phase Current { get; internal set; } = Phase.Setup;

    public IReadOnlyList<HarnessRecord> Records => _records;

    public void Detected(string detector, string text) => _records.Add(new DetectorEvent(Current, detector, text));

    public void Mark(string text) => _records.Add(new PhaseMark(Current, text));

    public void Measure(string name, double value, string unit) => _records.Add(new Measured(Current, name, value, unit));

    /// <summary>
    /// How often a detector fired. Counts <see cref="DetectorEvent"/> only, so no phase marker can add to
    /// its own total no matter what it says.
    /// </summary>
    public int DetectorCount(string detector, Phase? phase = null) =>
        _records.OfType<DetectorEvent>()
            .Count(e => e.Detector == detector && (phase is null || e.Phase == phase));

    /// <summary>The value of a measurement, or null when the run never took it.</summary>
    public double? ValueOf(string name) =>
        _records.OfType<Measured>().Where(m => m.Name == name).Select(m => (double?)m.Value).LastOrDefault();
}
