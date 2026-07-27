using Avalonia;
using Avalonia.Media;

namespace Cockpit.Plugins.Abstractions.Theming;

/// <summary>
/// Resolves a Cockpit theme brush by resource key from the host's live <see cref="Application"/> resources,
/// falling back to a literal colour only when none exist (a design-time preview or a test that never spun up the
/// host application). Shared by <see cref="Sessions.ProviderConfigStatus"/> and
/// <see cref="ManagedCli.ManagedCliConfigSection"/> so both track a host theme swap instead of each holding its
/// own frozen colour. Internal: this is plumbing for the SDK's own shared widgets, not part of the plugin contract.
/// A near-identical copy lives in <c>Cockpit.App.Theming.ThemeBrush</c>; the two are not merged into one shared
/// helper because doing so would require exposing it from this assembly as public SDK API — a permanent contract
/// for every plugin, needing a version bump to ever change. That merge, and cleaning up the SDK's own remaining
/// hand-written <c>_Brush</c> copies in <c>plugins-dev</c>, is a separate ticket.
/// </summary>
internal static class ThemeBrush
{
    public static IBrush Resolve(string key, string fallbackHex) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
