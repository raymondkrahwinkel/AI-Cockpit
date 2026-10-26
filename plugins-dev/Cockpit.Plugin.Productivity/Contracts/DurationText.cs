using System.Globalization;

namespace Cockpit.Plugin.Productivity.Contracts;

internal static class DurationText
{
    public static TimeSpan? Parse(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.EndsWith('m') && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        }

        if (value.EndsWith('u') && double.TryParse(
                value[..^1].Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var hours))
        {
            return double.IsFinite(hours) && hours > 0 && hours <= TimeSpan.MaxValue.TotalHours
                ? TimeSpan.FromHours(hours)
                : null;
        }

        var parts = value.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var clockHours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var clockMinutes)
            || clockHours < 0
            || clockMinutes is < 0 or > 59)
        {
            return null;
        }

        var duration = TimeSpan.FromHours(clockHours) + TimeSpan.FromMinutes(clockMinutes);
        return duration > TimeSpan.Zero ? duration : null;
    }
}
