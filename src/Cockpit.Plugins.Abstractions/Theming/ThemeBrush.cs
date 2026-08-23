using Avalonia;
using Avalonia.Media;

namespace Cockpit.Plugins.Abstractions.Theming;

/// <summary>
/// Resolves a Cockpit theme brush by resource key from the host's live <see cref="Application"/> resources,
/// falling back to a literal colour only when none exist. Internal plumbing for the SDK's own shared widgets,
/// not part of the plugin contract.
/// </summary>
/// <remarks>
/// Shared by <see cref="Sessions.ProviderConfigStatus"/> and <see cref="ManagedCli.ManagedCliConfigSection"/> so
/// both track a host theme swap.
/// </remarks>
internal static class ThemeBrush
{
    public static IBrush Resolve(string key, string fallbackHex) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
