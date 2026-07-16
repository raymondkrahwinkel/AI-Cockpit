using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Drives the two-phase plugin lifecycle across the app's DI bootstrap (#14). Phase 1
/// (<see cref="LoadAndConfigure"/>) runs before the container is built: it instantiates every
/// load-decided plugin and lets each register its own services. Phase 2 (<see cref="Initialize"/>) runs
/// once the container and UI exist: each plugin registers its contribution points through a host built
/// for it. Instantiation is a delegate seam so the orchestration is testable without real assembly
/// loading. One plugin that throws is logged and skipped — it never takes the app or its siblings down.
/// </summary>
internal sealed class PluginManager(ILogger<PluginManager> logger, PluginDiagnostics diagnostics) : IDisposable
{
    private readonly List<(DiscoveredPlugin Discovered, ICockpitPlugin Plugin)> _loaded = [];

    /// <summary>The plugins that actually loaded — their manifests, for the host to read what they declared (e.g. which storage keys hold a credential).</summary>
    public IReadOnlyList<DiscoveredPlugin> Loaded => [.. _loaded.Select(entry => entry.Discovered)];

    /// <summary>
    /// Phase 1 — before <c>BuildServiceProvider</c>: instantiate each <see cref="PluginLoadDecision.Load"/>
    /// plugin via <paramref name="activate"/> and run its <see cref="ICockpitPlugin.ConfigureServices"/>
    /// against the still-open <paramref name="services"/>. Plugins that fail to instantiate or configure
    /// are skipped (and disposed if they were created).
    /// </summary>
    public void LoadAndConfigure(
        IReadOnlyList<DiscoveredPlugin> discovered,
        IServiceCollection services,
        Func<DiscoveredPlugin, ICockpitPlugin?> activate)
    {
        foreach (var candidate in discovered)
        {
            if (candidate.Decision != PluginLoadDecision.Load)
            {
                continue;
            }

            ICockpitPlugin? plugin;
            try
            {
                plugin = activate(candidate);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} failed to load; skipping it.", candidate.FolderId);
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "load", exception.Message);
                continue;
            }

            if (plugin is null)
            {
                logger.LogWarning("Plugin {PluginId} did not yield an ICockpitPlugin; skipping it.", candidate.FolderId);
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "load", "The plugin did not yield an ICockpitPlugin.");
                continue;
            }

            try
            {
                plugin.ConfigureServices(services);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} threw during ConfigureServices; skipping it.", candidate.FolderId);
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "configure", exception.Message);
                plugin.Dispose();
                continue;
            }

            _loaded.Add((candidate, plugin));
        }
    }

    /// <summary>
    /// Phase 2 — after the container is built and the UI exists: give each loaded plugin the host built
    /// for it (via <paramref name="hostFor"/>, which carries that plugin's own storage) so it can register
    /// its contribution points. A plugin that throws here is logged and left out; the others still init.
    /// </summary>
    public void Initialize(Func<DiscoveredPlugin, ICockpitHost> hostFor)
    {
        foreach (var (discovered, plugin) in _loaded)
        {
            try
            {
                plugin.Initialize(hostFor(discovered));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} threw during Initialize; its contributions are skipped.", discovered.FolderId);
                diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "initialize", exception.Message);
            }
        }
    }

    public void Dispose()
    {
        foreach (var (discovered, plugin) in _loaded)
        {
            try
            {
                plugin.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} threw while disposing.", discovered.FolderId);
            }
        }

        _loaded.Clear();
    }
}
