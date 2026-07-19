namespace Cockpit.Core.UsagePill;

/// <summary>
/// User-configurable choice of which metrics the session header's usage pill shows, persisted under the
/// <c>usagePill</c> section of <c>cockpit.json</c> (same store pattern as the transcript-display settings).
/// A global preference applied to every session; the header renders one mini-pill per selected field that
/// the session actually has data for.
/// </summary>
public sealed record UsagePillSettings
{
    /// <summary>The metrics to show, in display order. Defaults to just the context window, the original behaviour.</summary>
    public IReadOnlyList<UsagePillField> VisibleFields { get; init; } = [UsagePillField.Context];
}
