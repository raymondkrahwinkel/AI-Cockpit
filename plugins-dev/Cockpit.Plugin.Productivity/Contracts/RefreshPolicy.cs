namespace Cockpit.Plugin.Productivity.Contracts;

// When the Uren and Overzicht screens refresh themselves, and how "last updated" reads (AC-1816).
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
            return "nog niet bijgewerkt";
        }

        var age = now - last.Value;
        return age.TotalSeconds < 10 ? "zojuist bijgewerkt"
            : age.TotalMinutes < 1 ? $"bijgewerkt {(int)age.TotalSeconds} s geleden"
            : age.TotalHours < 1 ? $"bijgewerkt {(int)age.TotalMinutes} min geleden"
            : $"bijgewerkt {(int)age.TotalHours} u geleden";
    }
}
