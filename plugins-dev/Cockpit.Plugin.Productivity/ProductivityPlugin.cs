using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.Productivity;

public sealed class ProductivityPlugin : ICockpitPlugin
{
    private IReadOnlyList<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "productivity",
        DisplayName: "Productivity",
        Author: "Cockpit",
        Description: "Record your own time with quick entries or one persistent stopwatch, and review it by day.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host) =>
        _handlers = new ProductivityChannelHandlers(host, host.TimeEntries).Register();

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }
    }
}
