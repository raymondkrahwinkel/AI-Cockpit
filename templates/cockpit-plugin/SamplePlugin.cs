using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Sample;

// A starter Cockpit plugin, the backend part: what works without a window (services, MCP servers, providers,
// workflow steps) and the channel actions its UI part (UI/SampleUi.cs) invokes. See docs/plugins/PLUGIN-SDK.md.
public sealed class SamplePlugin : ICockpitPlugin
{
    private IDisposable? _greeting;

    public PluginMetadata Metadata { get; } = new(
        Id: "sample",
        DisplayName: "Sample",
        Author: "You",
        Description: "A starter Cockpit plugin — replace this with your own.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register your own services here (runs before the host container is built).
    }

    public void Initialize(ICockpitHost host)
    {
        // The UI part asks for this over the channel: JSON in, JSON out, so it keeps working when the two parts
        // run in different processes. Register backend-only contributions here too (AddSessionProvider, AddMcpServer).
        _greeting = host.Channel.Handle("greeting", (_, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { text = "Hello from my plugin!" })));
    }

    public void Dispose()
    {
        _greeting?.Dispose();
    }
}
