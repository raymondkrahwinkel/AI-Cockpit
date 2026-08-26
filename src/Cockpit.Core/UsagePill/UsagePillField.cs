namespace Cockpit.Core.UsagePill;

// A metric the session header's usage pill can surface. `RateWindows` (#1105 A2) draws one segment per
// rolling allowance window the provider reports — 5h+wk for Claude, 7d for Codex — rather than one enum
// value per window shape, which is what let Codex's weekly window fall through unrendered.
public enum UsagePillField
{
    Context,
    SessionUsage,
    RateWindows,
}
