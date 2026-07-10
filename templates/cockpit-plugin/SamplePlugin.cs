using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Sample;

/// <summary>
/// A starter Cockpit plugin. It contributes a left-menu button that opens a dialog; add a settings view
/// with <c>host.AddSettings(...)</c> (opened from the manager's gear), register your own services in
/// <see cref="ConfigureServices"/>, and persist settings via <c>host.Storage</c>. See docs/plugins/PLUGIN-SDK.md.
/// </summary>
public sealed class SamplePlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "sample",
        DisplayName: "Sample",
        Version: "1.0.0",
        Author: "You",
        Description: "A starter Cockpit plugin — replace this with your own.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register your own services here (runs before the host container is built).
    }

    public void Initialize(ICockpitHost host)
    {
        // A left-menu button that opens the plugin's content in a dialog. For plugin settings, also call
        // host.AddSettings(() => new YourSettingsControl(...)) — it appears behind the gear in the manager.
        host.AddSideMenuButton("Sample", () => _ = host.ShowDialogAsync("Sample", () => new SamplePanelControl(host)));
    }

    public void Dispose()
    {
    }
}
