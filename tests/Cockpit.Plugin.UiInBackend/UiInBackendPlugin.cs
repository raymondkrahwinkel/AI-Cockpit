using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.UiInBackend;

// AC-1403: the backend part a server must refuse. Initialize names an Avalonia type, so the JIT resolves Avalonia the
// moment the host calls it.
public sealed class UiInBackendPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "ui-in-backend",
        DisplayName: "UI in backend",
        Author: "Cockpit",
        Description: "Test fixture: a backend part that touches Avalonia.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _ = typeof(Avalonia.Controls.Control).Name;
    }

    public void Dispose()
    {
    }
}
