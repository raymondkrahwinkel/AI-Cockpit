using System.Globalization;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// The letter a project without a logo shows in its place (AC-162): the first character of its name, upper-cased.
/// Something in the well beats an empty box — the card keeps its shape whether or not a logo was ever chosen.
/// </summary>
public sealed class ProjectInitialConverter : IValueConverter
{
    public static readonly ProjectInitialConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name && name.Trim() is { Length: > 0 } trimmed
            ? trimmed[..1].ToUpper(culture)
            : "·";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
