using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// Formats when a project was last worked on for its card in the projects overview (AC-162): "Opened just now",
/// "Opened 3 days ago", or "Not opened yet" for one that has only ever been set up. Deliberately relative and in
/// plain words — the card is read by someone deciding where to carry on, and a timestamp answers a question they
/// did not ask.
/// </summary>
public sealed class LastOpenedConverter : IValueConverter
{
    public static readonly LastOpenedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset openedAt)
        {
            return "Not opened yet";
        }

        // No conversion to local time first: subtracting two DateTimeOffsets compares the instants they stand for,
        // whatever offsets they carry, so a project opened on a machine an hour away still reads its true age.
        var elapsed = DateTimeOffset.Now - openedAt;

        // A clock that has moved back (a manual change, a restored config) reads as just now rather than as a
        // negative age: "opened -3 days ago" is nonsense the operator would have to interpret.
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "Opened just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"Opened {(int)elapsed.TotalMinutes} min ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "Opened 1 hour ago" : $"Opened {hours} hours ago";
        }

        var days = (int)elapsed.TotalDays;
        return days == 1 ? "Opened yesterday" : $"Opened {days} days ago";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
