using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Converters;

/// <summary>True only for <see cref="SessionStatus.NeedsAttention"/> — drives the sidebar's attention glyph.</summary>
public sealed class IsNeedsAttentionConverter : IValueConverter
{
    public static readonly IsNeedsAttentionConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SessionStatus.NeedsAttention;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
