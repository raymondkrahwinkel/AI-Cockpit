using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Schedules.Contracts;

// A reminder (AC-1549, plan §2): a text at a moment in an IANA zone, once or repeating in the schedule forms a task uses,
// delivered to the chosen channels. Open until its moment, due once it went off, done when ticked off; a repeating one
// goes back to open on its next moment, with the ones done kept in Done.
public sealed record Reminder
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    // `once yyyy-MM-dd HH:mm`, or a 5-field cron for one that repeats.
    public required string Schedule { get; init; }

    public required string TimeZone { get; init; }

    public ReminderState State { get; init; } = ReminderState.Open;

    // The moment of this occurrence.
    public required DateTimeOffset Slot { get; init; }

    public DateTimeOffset? SnoozedUntil { get; init; }

    // When it last went off, and whether that was late: "missed, was 09:00".
    public DateTimeOffset? FiredAt { get; init; }

    public IReadOnlyList<DeliveryTarget> Delivery { get; init; } = [new DeliveryTarget(DeliveryTarget.CockpitNotification)];

    // Who made it: "Created by you", "Created by an agent over MCP".
    public string? Origin { get; init; }

    // What the last delivery came to, such as "Discord: plugin not installed → handed to the assistant ✓".
    public string? DeliveryNote { get; init; }

    // The occurrences ticked off, newest first; a one-off has at most its own.
    public IReadOnlyList<DateTimeOffset> Done { get; init; } = [];

    [JsonIgnore]
    public bool Repeats => !Schedule.TrimStart().StartsWith("once", StringComparison.OrdinalIgnoreCase);

    // The moment it goes off next: a snooze moves it, nothing else does.
    [JsonIgnore]
    public DateTimeOffset Due => SnoozedUntil ?? Slot;
}

public enum ReminderState
{
    Open,
    Due,
    Done,
}
