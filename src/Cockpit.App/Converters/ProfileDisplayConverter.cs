using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Converters;

// Renders a profile label with its provider/model (#26), so the picker shows the backend without a wrapper view
// model; plugin providers resolve their specific name rather than the generic "Plugin" placeholder.
public sealed class ProfileDisplayConverter : IValueConverter
{
    public static readonly ProfileDisplayConverter Instance = new();

    // Static because the x:Static converter is not DI-constructed; until startup wiring (or in the previewer),
    // plugin profiles fall back to "Plugin".
    public static IPluginProviderRegistry? PluginProviderRegistry { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionProfile profile
            ? ProfileDisplay.Format(profile.Label, profile.Provider, ProfileDisplay.ModelOf(profile), PluginProviderName(profile))
            : value?.ToString();

    // The specific plugin provider's display name for a Plugin-provider profile, or null for a built-in provider.
    private static string? PluginProviderName(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin
            ? PluginProviderRegistry?.Resolve(plugin.ProviderId)?.DisplayName
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
