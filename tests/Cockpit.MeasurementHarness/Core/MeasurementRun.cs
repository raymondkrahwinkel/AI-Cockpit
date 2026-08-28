using System.Text;

namespace Cockpit.MeasurementHarness.Core;

/// <summary>A condition that has to hold before a run's numbers mean anything.</summary>
public sealed record Gate(string Name, Func<bool> Satisfied, string Failure);

/// <summary>The end state of a run: a measurement, or a malfunction that happens to contain numbers.</summary>
public sealed record RunOutcome(bool Trustworthy, string Verdict, string Report, IReadOnlyList<string> Blockers);

/// <summary>
/// One measurement run. Holds the phases (E3), the positive control (E1) and the gates, and writes the
/// report (E4). A scenario supplies what to measure; everything about whether the result may be believed
/// lives here, so no scenario can leave it out by forgetting to add it.
/// </summary>
public sealed class MeasurementRun
{
    private readonly List<Gate> _gates = [];
    private readonly List<ShapeVerdict> _shapes = [];
    private readonly List<string> _lines = [];
    private readonly PositiveControl _control;
    private ControlOutcome? _controlOutcome;
    private bool _verifyStarted;

    /// <summary>
    /// A run cannot be constructed without a positive control. That is the whole point of it being an
    /// argument: `ac1104` had one and it silently stopped working, because nothing needed it to exist.
    /// </summary>
    public MeasurementRun(RunIdentity identity, PositiveControl control)
    {
        Identity = identity;
        _control = control;
    }

    public RunIdentity Identity { get; }

    public Recorder Recorder { get; } = new();

    public Phase Phase => Recorder.Current;

    /// <summary>Adds a condition the run has to satisfy, checked when the report is built.</summary>
    public void Gate(string name, Func<bool> satisfied, string failure) =>
        _gates.Add(new Gate(name, satisfied, failure));

    /// <summary>Records a shape test over a swept variable (E5). Its verdict lands in the header.</summary>
    public void Shape(ShapeVerdict verdict) => _shapes.Add(verdict);

    /// <summary>Adds a line to the report's body.</summary>
    public void Write(string line) => _lines.Add(line);

    /// <summary>
    /// Runs the measurement itself. Refused once verification has begun, because a measurement taken
    /// after a forced full GC is a measurement of that GC.
    /// </summary>
    public async Task MeasureAsync(string what, Func<Recorder, Task> body)
    {
        if (_verifyStarted)
        {
            throw new InvalidOperationException(
                $"'{what}' tried to measure after verification began. Verification is last by construction (E3): "
                + "a forced full GC took 17,1 s on a 10,2 GB heap and set off the app's own uifreeze detector.");
        }

        Recorder.Current = Phase.Measure;
        Recorder.Mark($"measure: {what}");
        await body(Recorder).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs the expensive checks, after the measurement window has closed. This is the only place a full
    /// blocking GC, a dump or anything else that costs seconds is allowed to happen.
    /// </summary>
    public void Verify(string what, Action<Recorder> body)
    {
        _verifyStarted = true;
        Recorder.Current = Phase.Verify;
        Recorder.Mark($"verify: {what}");
        body(Recorder);
    }

    /// <summary>Runs the positive control, in this same run and on these same flags.</summary>
    public async Task<ControlOutcome> RunControlAsync()
    {
        _controlOutcome = await _control.RunAsync(Recorder).ConfigureAwait(true);
        return _controlOutcome;
    }

    /// <summary>
    /// Builds the report and the verdict. Throws when the control was never run — a report without one is
    /// the thing this harness exists to make impossible.
    /// </summary>
    public RunOutcome Finish()
    {
        if (_controlOutcome is null)
        {
            throw new InvalidOperationException(
                "RunControlAsync() was never called. A run that did not exercise its positive control cannot tell "
                + "a zero apart from a blind instrument, so it has no report to write.");
        }

        var blockers = new List<string>();
        if (!_controlOutcome.Trustworthy)
        {
            blockers.Add($"positive control '{_controlOutcome.Name}' did not fire");
        }

        blockers.AddRange(_gates.Where(g => !g.Satisfied()).Select(g => $"{g.Name}: {g.Failure}"));
        blockers.AddRange(_shapes.Where(s => !s.Holds).Select(s => s.Line));

        var trustworthy = blockers.Count == 0;
        var verdict = trustworthy ? "MEASUREMENT" : "MALFUNCTION";

        var sb = new StringBuilder();
        foreach (var line in Identity.HeaderLines())
        {
            sb.AppendLine(line);
        }

        sb.AppendLine(_controlOutcome.HeaderLine);
        foreach (var shape in _shapes)
        {
            sb.AppendLine(shape.Line);
        }

        sb.AppendLine($"VERDICT: {verdict}");
        foreach (var blocker in blockers)
        {
            sb.AppendLine($"  blocked by: {blocker}");
        }

        sb.AppendLine();
        foreach (var line in _lines)
        {
            sb.AppendLine(line);
        }

        return new RunOutcome(trustworthy, verdict, sb.ToString(), blockers);
    }
}
