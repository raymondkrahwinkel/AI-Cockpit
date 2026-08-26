using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

// Formats the usage-pill reset hover (AC-37) with relative and local times; no provider reset time means no line,
// rather than a placeholder.
public sealed class RateWindowResetConverter : IValueConverter
{
    public static readonly RateWindowResetConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset resetsAt)
        {
            return string.Empty;
        }

        var local = resetsAt.ToLocalTime();
        var absolute = local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        var remaining = local - DateTimeOffset.Now;

        if (remaining <= TimeSpan.Zero)
        {
            return $"resetting… · {absolute}";
        }

        return $"resets in {_Relative(remaining)} · {absolute}";
    }

    // Coarsest-first, at most two units: a reset a day out reads "1d 3h", one minutes away reads "14m".
    private static string _Relative(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
