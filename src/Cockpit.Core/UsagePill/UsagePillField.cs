namespace Cockpit.Core.UsagePill;

// A metric the session header's usage pill can surface. `Context`, `FiveHourWindow`
// and `WeeklyWindow` carry a percentage and are threshold-coloured; `SessionUsage` is
// the running token/cost total and shows without a severity colour.
public enum UsagePillField
{
    Context,
    SessionUsage,
    FiveHourWindow,
    WeeklyWindow,
}
