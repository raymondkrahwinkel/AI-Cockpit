using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Layout;

// Persists `LayoutSettings` under the `layout` section of `cockpit.json` (same pattern as
// `SessionBehaviorSettingsStore`), read-modify-write via `CockpitConfigFileAccess` so other sections
// stay untouched. `LoadAsync` returns the defaults when no settings were ever saved.
internal sealed class LayoutSettingsStore : ILayoutSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public LayoutSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal LayoutSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<LayoutSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var settings = configFile?.Layout?.ToDomain() ?? new LayoutSettings();

        // Clamp defensively on load too, not just when the splitter drags — a hand-edited cockpit.json
        // could hold a stale or out-of-range value from before the min/max were introduced.
        return settings with
        {
            SidebarWidth = Math.Clamp(settings.SidebarWidth, LayoutSettings.MinSidebarWidth, LayoutSettings.MaxSidebarWidth),
            FocusRailWeight = Math.Clamp(settings.FocusRailWeight, LayoutSettings.MinFocusRailWeight, LayoutSettings.MaxFocusRailWeight),
            DockRailWidth = Math.Clamp(settings.DockRailWidth, LayoutSettings.MinDockRailWidth, LayoutSettings.MaxDockRailWidth),
        };
    }

    public Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default)
    {
        var clamped = settings with
        {
            SidebarWidth = Math.Clamp(settings.SidebarWidth, LayoutSettings.MinSidebarWidth, LayoutSettings.MaxSidebarWidth),
            FocusRailWeight = Math.Clamp(settings.FocusRailWeight, LayoutSettings.MinFocusRailWeight, LayoutSettings.MaxFocusRailWeight),
            DockRailWidth = Math.Clamp(settings.DockRailWidth, LayoutSettings.MinDockRailWidth, LayoutSettings.MaxDockRailWidth),
        };

        return _configFile.UpdateAsync(
            file => file.Layout = LayoutSettingsEntry.FromDomain(clamped),
            cancellationToken);
    }
}
