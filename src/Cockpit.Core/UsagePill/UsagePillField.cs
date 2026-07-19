namespace Cockpit.Core.UsagePill;

/// <summary>
/// A metric the session header's usage pill can surface. <see cref="Context"/>, <see cref="FiveHourWindow"/>
/// and <see cref="WeeklyWindow"/> carry a percentage and are threshold-coloured; <see cref="SessionUsage"/> is
/// the running token/cost total and shows without a severity colour.
/// </summary>
public enum UsagePillField
{
    Context,
    SessionUsage,
    FiveHourWindow,
    WeeklyWindow,
}
