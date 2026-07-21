namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The outcome of the pre-start scoping judgment (AC-151, decision #3): a point is workable, or refused with a reason
/// that gets parked back onto it. Built from a delegated judge's free-text answer.
/// </summary>
internal readonly record struct ScopingVerdict(bool IsWorkable, string Reason)
{
    public static ScopingVerdict Workable { get; } = new(true, string.Empty);

    public static ScopingVerdict Refuse(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason) ? "Scoping refused this point." : reason);

    /// <summary>
    /// The judge is asked to answer with <c>WORKABLE</c> or <c>REFUSE: &lt;reason&gt;</c> on the first line. Anything
    /// else reads as workable, so a judge that goes off-script never blocks a point the operator explicitly started.
    /// </summary>
    public static ScopingVerdict Parse(string answer)
    {
        var firstLine = (answer ?? string.Empty).Split('\n', 2)[0].Trim();
        return firstLine.StartsWith("REFUSE", StringComparison.OrdinalIgnoreCase)
            ? Refuse(firstLine[6..].TrimStart(':', ' '))
            : Workable;
    }
}
