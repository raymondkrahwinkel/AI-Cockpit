namespace Cockpit.Core.UsagePill;

// User-configurable choice of which metrics the session header's usage pill shows, persisted under the
// `usagePill` section of `cockpit.json` (same store pattern as the transcript-display settings).
// A global preference applied to every session; the header renders one mini-pill per selected field that
// the session actually has data for.
public sealed record UsagePillSettings
{
    // The metrics to show, in display order. Defaults to just the context window, the original behaviour.
    public IReadOnlyList<UsagePillField> VisibleFields { get; init; } = [UsagePillField.Context];
}
