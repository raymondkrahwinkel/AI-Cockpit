using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Converters;

// True only for `SessionStatus.NeedsAttention` — drives the sidebar's attention glyph.
public sealed class IsNeedsAttentionConverter : IValueConverter
{
    public static readonly IsNeedsAttentionConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SessionStatus.NeedsAttention;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
