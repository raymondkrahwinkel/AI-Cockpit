namespace Cockpit.Plugin.Schedules.Contracts;

// Where a run's result goes: a channel and, for a channel that has them, a target such as a DM or a room. This build
// knows only the cockpit notification; the channels of communication plugins come with A3 (AC-1544).
public sealed record DeliveryTarget(string Channel, string? Target = null)
{
    public const string CockpitNotification = "cockpit";
}
