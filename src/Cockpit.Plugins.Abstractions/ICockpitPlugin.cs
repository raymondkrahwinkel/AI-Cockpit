using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The entry point a Cockpit plugin implements, loaded from its own folder under the config-dir's
/// <c>plugins/</c> directory. Two-phase lifecycle: <see cref="ConfigureServices"/> runs before the
/// host's DI container is built (register the plugin's own services), then <see cref="Initialize"/>
/// runs once the host is up (register contribution points via <see cref="ICockpitHost"/>). Disposed
/// when the plugin is disabled or the app shuts down — note the assembly itself is only freed on
/// restart, since a loaded plugin cannot be truly unloaded.
/// </summary>
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }

    /// <summary>Phase 1, before the container is built: register the plugin's own services.</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>Phase 2, after the container is built: register contribution points (Options tab, side-menu section) via the host.</summary>
    void Initialize(ICockpitHost host);
}
