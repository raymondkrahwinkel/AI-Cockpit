using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Schedules.Contracts;

// AC-1546: what the backend part answers the screen over the plugin's channel, the same operations cockpit-schedules
// offers an agent (A4). Linked into the UI part as source, so the screen works the same when the backend is a server.
internal static class SchedulesChannel
{
    // Payload: none. Answers a SchedulesSnapshot.
    public const string Snapshot = "snapshot";

    // Payload: ScheduledTask. Saves it as the operator wrote it, new or replacing the one with its id; answers a SchedulesResult.
    public const string Save = "save";

    // Payload: SchedulesTaskRequest. Answers a SchedulesResult.
    public const string Delete = "delete";

    // Payload: SchedulesEnableRequest. The operator's own switch, so it asks nothing; answers a SchedulesResult.
    public const string SetEnabled = "set-enabled";

    // Payload: SchedulesTaskRequest. Runs it now as the operator; answers the RunRecord as it starts, or null.
    public const string RunNow = "run-now";

    // Payload: ScheduledTask. Runs it once without saving, delivering to the cockpit only; answers the RunRecord.
    public const string TestRun = "test-run";

    // Payload: SchedulesPreviewRequest. Answers a SchedulesPreview: whether the schedule reads, in words, and its next runs.
    public const string Preview = "preview";

    // Payload: SchedulesSettingsView. Saves the engine settings; answers a SchedulesResult.
    public const string SaveSettings = "save-settings";

    // Event, payload RunRecord: a run of any origin was recorded or finished.
    public const string RunRecorded = "run-recorded";

    // Event, payload int: how many reminders went off and wait (AC-1549), on every change and every tick, for the badge.
    public const string RemindersDue = "reminders-due";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed record SchedulesSnapshot(
    List<SchedulesTaskView> Tasks,
    List<RunRecord> Runs,
    List<OperatorMessageChannelInfo> Channels,
    SchedulesSettingsView Settings,
    DateTimeOffset? Now = null);

// A task as the list shows it: the task, its schedule in words, and when it runs next (null when off or unreadable).
internal sealed record SchedulesTaskView(ScheduledTask Task, string? ReadsAs, DateTimeOffset? NextRun);

internal sealed record SchedulesSettingsView(int MaxConcurrentRuns, string DefaultTimeZone, int KeepDays);

internal sealed record SchedulesResult(bool Ok, string? Error = null, string? Id = null);

internal sealed record SchedulesTaskRequest(string Id);

internal sealed record SchedulesEnableRequest(string Id, bool Enabled, bool RunUnattended);

internal sealed record SchedulesPreviewRequest(string Schedule, string TimeZone);

internal sealed record SchedulesPreview(bool Valid, string? ReadsAs, List<DateTimeOffset> Next);
