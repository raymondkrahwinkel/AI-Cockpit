namespace Cockpit.Plugin.Schedules.Contracts;

// Where a run's result goes: the cockpit notification, or the channel of a communication plugin (AC-1544) with one of
// its targets, such as a DM or a room. Without a target the channel's first is used.
public sealed record DeliveryTarget(string Channel, string? Target = null)
{
    public const string CockpitNotification = "cockpit";
}
