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
        Description: "Record your time and review time, tokens and costs by project, ticket and session over a selected range.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var reportGate = new SemaphoreSlim(1, 1);
        _handlers = new ProductivityChannelHandlers(host, host.TimeEntries, reportGate).Register();
        _ = _RegisterToolsAsync(host, reportGate);
    }

    private static async Task _RegisterToolsAsync(ICockpitHost host, SemaphoreSlim reportGate)
    {
        try
        {
            await host.AddMcpEndpoint("cockpit-productivity", new ProductivityMcpTools(new HoursResultReporter(host, reportGate))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            host.ShowToast($"De uren-terugmelding kon niet worden gestart: {exception.Message}", Cockpit.Plugins.Abstractions.Notifications.PluginToastSeverity.Error);
        }
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }
    }
}
