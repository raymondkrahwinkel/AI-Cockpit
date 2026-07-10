using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Sample;

/// <summary>
/// A starter Cockpit plugin. It contributes one left-menu section; add an Options tab with
/// <c>host.AddOptionsTab(...)</c>, register your own services in <see cref="ConfigureServices"/>, and
/// persist settings via <c>host.Storage</c>. See docs/plugins/PLUGIN-SDK.md.
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
        host.AddSideMenuSection("Sample", () => new SamplePanelControl(host));
    }

    public void Dispose()
    {
    }
}
