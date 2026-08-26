namespace Cockpit.Core.UsagePill;

// Global choice of session-header usage metrics, persisted under `usagePill` in `cockpit.json`.
// Each session renders a mini-pill only for selected fields it has data for.
public sealed record UsagePillSettings
{
    // The metrics to show, in display order. Defaults to just the context window, the original behaviour.
    public IReadOnlyList<UsagePillField> VisibleFields { get; init; } = [UsagePillField.Context];
}
