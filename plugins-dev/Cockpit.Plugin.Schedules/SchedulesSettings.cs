using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules;

// The plugin's own settings, read live so a change takes effect on the next run without a restart.
internal sealed class SchedulesSettings(IPluginStorage storage)
{
    private const string MaxConcurrentRunsKey = "max-concurrent-runs";
    private const string DefaultTimeZoneKey = "default-time-zone";
    private const int DefaultMaxConcurrentRuns = 2;

    public int MaxConcurrentRuns
    {
        get => Math.Max(1, storage.Get<int?>(MaxConcurrentRunsKey) ?? DefaultMaxConcurrentRuns);
        set => storage.Set(MaxConcurrentRunsKey, Math.Max(1, value));
    }

    // The zone a new task starts in: the operator's choice, else this machine's own zone by its IANA name.
    public string DefaultTimeZone
    {
        get => storage.Get<string>(DefaultTimeZoneKey) is { Length: > 0 } chosen ? chosen : MachineTimeZone();
        set => storage.Set(DefaultTimeZoneKey, value);
    }

    public static string MachineTimeZone() =>
        TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";
}
