using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// Formats a usage window's reset time for the session header's usage-pill hover (AC-37): a relative
/// "resets in 2h 14m" (the glanceable part) plus the absolute local time "Thu 14:30" (the detail). Returns an
/// empty string when the provider gave no reset time (<see cref="Cockpit.Core.Sessions.SessionRateWindow.ResetsAt"/>
/// is null), so the row simply shows the bar without a reset line rather than a placeholder.
/// </summary>
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
