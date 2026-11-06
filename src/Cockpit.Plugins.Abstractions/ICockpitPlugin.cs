using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The entry point a Cockpit plugin implements, loaded from its own folder under the config-dir's
/// <c>plugins/</c> directory. Two-phase lifecycle: <see cref="ConfigureServices"/> runs before the host's DI
/// container is built, then <see cref="Initialize"/> runs once the host is up.
/// </summary>
/// <remarks>
/// Disposed when the plugin is disabled live or the app shuts down; the assembly itself stays loaded until a restart.
/// The host has by then withdrawn everything registered through it: contributions, MCP endpoints and channel
/// handlers. <see cref="IDisposable.Dispose"/> stops what the plugin started itself: timers, pollers, connections,
/// runs, and every C# event it subscribed to on the host or a host object. It may be enabled again in the same
/// process, in a new instance. It runs off the UI thread, and a live disable moves on after 10 seconds without
/// waiting, so cancel the work rather than wait for it. A plugin that cannot do this sets <c>requiresRestart</c>.
/// </remarks>
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }

    /// <summary>
    /// Phase 1, before the container is built: register the plugin's own services.
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Phase 2, after the container is built: register contribution points (Options tab, side-menu section) via the host.
    /// </summary>
    void Initialize(ICockpitHost host);
}
