namespace Cockpit.MeasurementHarness.Core;

/// <summary>
/// E1: a case that has to go off, so a zero can be told apart from a blind instrument. Held as a value
/// because a second call you can forget is a second call that gets forgotten — that is how the control of
/// `ac1104` sat broken through every conclusion it produced, reporting a clean zero (AC-1171).
/// </summary>
public sealed class PositiveControl
{
    private readonly Func<Recorder, Task<bool>>? _run;

    private PositiveControl(string name, string reason, Func<Recorder, Task<bool>>? run)
    {
        Name = name;
        Reason = reason;
        _run = run;
    }

    public string Name { get; }

    /// <summary>Why there is no control, when there is none. Empty for a real one.</summary>
    public string Reason { get; }

    public bool IsDeclaredAbsent => _run is null;

    /// <summary>A control that runs in the same run, on the same flags, and says whether it went off.</summary>
    public static PositiveControl Named(string name, Func<Recorder, Task<bool>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        return new PositiveControl(name, string.Empty, run);
    }

    /// <summary>
    /// No control, on the record. Legal, because forcing one would only produce a fake — but the reason
    /// ends up in the report header, where it can be argued with instead of quietly assumed.
    /// </summary>
    public static PositiveControl None(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PositiveControl("none", reason, null);
    }

    /// <summary>Runs the control. An absent one neither fires nor pretends to.</summary>
    public async Task<ControlOutcome> RunAsync(Recorder recorder)
    {
        if (_run is null)
        {
            return new ControlOutcome(Name, Fired: false, Declared: true, Reason);
        }

        var fired = await _run(recorder).ConfigureAwait(true);
        return new ControlOutcome(Name, fired, Declared: false, Reason);
    }
}

/// <summary>What the control did, as it appears in the report header next to the result.</summary>
public sealed record ControlOutcome(string Name, bool Fired, bool Declared, string Reason)
{
    /// <summary>True when this run's numbers are worth reading at all.</summary>
    public bool Trustworthy => Fired || Declared;

    public string HeaderLine => Declared
        ? $"POSITIVE CONTROL: none declared — {Reason}"
        : Fired
            ? $"POSITIVE CONTROL: {Name} FIRED"
            : $"POSITIVE CONTROL: {Name} DID NOT FIRE — this run is a malfunction, not a measurement";
}
