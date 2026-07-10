using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.App.Converters;

/// <summary>
/// Renders a <see cref="ClaudeProfile"/> as its display label with provider (and local model) — e.g.
/// <c>default (Claude CLI)</c> / <c>local (LM Studio - qwen2.5)</c> — so the New-session profile picker
/// shows the backend without a wrapper view model (#26).
/// </summary>
public sealed class ProfileDisplayConverter : IValueConverter
{
    public static readonly ProfileDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ClaudeProfile profile
            ? ProfileDisplay.Format(profile.Label, profile.Provider, ProfileDisplay.ModelOf(profile))
            : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
