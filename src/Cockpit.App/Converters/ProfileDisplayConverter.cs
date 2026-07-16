using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Converters;

/// <summary>
/// Renders a <see cref="SessionProfile"/> as its display label with provider (and local model) — e.g.
/// <c>default (Claude) / local (LM Studio - qwen2.5)</c> — so the New-session profile picker shows the backend
/// without a wrapper view model (#26). A plugin-provider profile shows the specific plugin's own display name
/// (resolved through <see cref="PluginProviderRegistry"/>) rather than the generic "Plugin" placeholder.
/// </summary>
public sealed class ProfileDisplayConverter : IValueConverter
{
    public static readonly ProfileDisplayConverter Instance = new();

    /// <summary>
    /// The provider registry the converter resolves a plugin profile's own display name through, set once at app
    /// startup (a static seam because an <see cref="IValueConverter"/> used via <c>x:Static Instance</c> is not
    /// DI-constructed). Null until wired, or in the design-time previewer — a plugin profile then falls back to the
    /// generic "Plugin" label, exactly as before.
    /// </summary>
    public static IPluginProviderRegistry? PluginProviderRegistry { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionProfile profile
            ? ProfileDisplay.Format(profile.Label, profile.Provider, ProfileDisplay.ModelOf(profile), PluginProviderName(profile))
            : value?.ToString();

    /// <summary>The specific plugin provider's display name for a Plugin-provider profile, or null for a built-in provider.</summary>
    private static string? PluginProviderName(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig plugin
            ? PluginProviderRegistry?.Resolve(plugin.ProviderId)?.DisplayName
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
