namespace Cockpit.Plugin.Productivity.Contracts;

// When the Hours and Overview screens refresh themselves, and how "last updated" reads (AC-1816).
internal static class RefreshPolicy
{
    public const int DefaultSeconds = 60;

    public static TimeSpan Interval(int seconds) =>
        TimeSpan.FromSeconds(seconds is >= 15 and <= 3600 ? seconds : DefaultSeconds);

    // Never while the operator is editing or a refresh is already running.
    public static bool IsDue(bool visible, bool editing, bool refreshing, DateTimeOffset? last, DateTimeOffset now, TimeSpan interval) =>
        visible && !editing && !refreshing && (last is null || now - last.Value >= interval);

    public static string Age(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is null)
        {
            return "not updated yet";
        }

        var age = now - last.Value;
        return age.TotalSeconds < 10 ? "updated just now"
            : age.TotalMinutes < 1 ? $"updated {(int)age.TotalSeconds} s ago"
            : age.TotalHours < 1 ? $"updated {(int)age.TotalMinutes} min ago"
            : $"updated {(int)age.TotalHours} h ago";
    }
}
