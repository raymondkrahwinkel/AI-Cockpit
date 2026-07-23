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
internal sealed class PluginManager(
    ILogger<PluginManager> logger,
    PluginDiagnostics diagnostics,
    Version? hostAbstractionsVersion = null,
    Func<ICockpitPlugin, Version?>? builtAgainstResolver = null) : IDisposable
{
    private readonly List<(DiscoveredPlugin Discovered, ICockpitPlugin Plugin)> _loaded = [];

    // The abstractions version this app actually ships, and how to read the one a plugin was built against —
    // both seams so a test can drive the drift check without a purpose-built assembly. The defaults are the
    // real thing: the host's own Cockpit.Plugins.Abstractions, and the version baked into the plugin's assembly
    // reference at compile time (which no manifest can misstate).
    private readonly Version _hostAbstractions =
        hostAbstractionsVersion ?? typeof(AbstractionsContract).Assembly.GetName().Version ?? new Version(0, 0);

    private readonly Func<ICockpitPlugin, Version?> _builtAgainst = builtAgainstResolver ?? _ReadBuiltAgainstAbstractions;

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
                _NoteSkipped(candidate);
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
            _WarnIfBuiltAgainstNewerHost(candidate, plugin);
        }
    }

    // A plugin discovery decided not to load leaves no other trace — the loop simply skips it — so a provider that
    // silently vanished (a Claude update that dropped to needs-consent, an abstractions mismatch) became an
    // unexplained "no such provider" downstream with nothing in the log to explain it. This is that breadcrumb.
    // The refused decisions (abstractions/host) and awaiting-consent are also recorded for the startup banner and
    // plugin manager to surface (AC-208 added the latter); disabled is a log line only — the manager already shows
    // that state, and an operator who disabled a plugin does not need reminding.
    private void _NoteSkipped(DiscoveredPlugin candidate)
    {
        switch (candidate.Decision)
        {
            case PluginLoadDecision.AbstractionsMajorMismatch:
                logger.LogWarning(
                    "Plugin {PluginId} was built against a different Cockpit contract major and was not loaded.",
                    candidate.FolderId);
                diagnostics.Record(
                    candidate.FolderId, candidate.Manifest.Name, "load",
                    "Built against a different Cockpit contract version than this app — update the app or reinstall the plugin build made for it.",
                    PluginIssueSeverity.Warning);
                break;

            case PluginLoadDecision.HostTooOld:
                logger.LogWarning(
                    "Plugin {PluginId} needs a newer cockpit than this one (its minHostVersion) and was not loaded.",
                    candidate.FolderId);
                diagnostics.Record(
                    candidate.FolderId, candidate.Manifest.Name, "load",
                    "This plugin needs a newer version of the app than you are running — update the app to use it.",
                    PluginIssueSeverity.Warning);
                break;

            case PluginLoadDecision.NeedsConsent:
                logger.LogInformation(
                    "Plugin {PluginId} is awaiting approval (new, or its bytes changed since you approved it) and was not loaded until you approve it in Plugin Manager.",
                    candidate.FolderId);
                // AC-208: also register it, so the startup banner and the plugin-store badge can count it — the
                // log line alone left this state invisible until the operator happened to open Plugin Manager.
                diagnostics.RecordPendingApproval(candidate.FolderId, candidate.Manifest.Name);
                break;

            case PluginLoadDecision.Disabled:
                logger.LogInformation("Plugin {PluginId} is disabled and was not loaded.", candidate.FolderId);
                break;
        }
    }

    // The plugin loaded — the reference to Cockpit.Plugins.Abstractions resolved to the host's own copy. But if
    // it was compiled against a newer SDK than this app ships, it may call a member that copy does not have, and
    // that fails somewhere the operator never sees. It stays loaded (an older app running a plugin from a newer
    // one usually works), but this says so out loud instead of leaving it to surface as an unexplained throw.
    private void _WarnIfBuiltAgainstNewerHost(DiscoveredPlugin candidate, ICockpitPlugin plugin)
    {
        var builtAgainst = _builtAgainst(plugin);
        if (!AbstractionsCompatibility.BuiltAgainstNewerHost(builtAgainst, _hostAbstractions))
        {
            return;
        }

        logger.LogWarning(
            "Plugin {PluginId} was built against Cockpit SDK {BuiltAgainst}, newer than this app's {Host}; it is loaded but may call members this app does not have.",
            candidate.FolderId, builtAgainst, _hostAbstractions);
        diagnostics.Record(
            candidate.FolderId,
            candidate.Manifest.Name,
            "compatibility",
            $"Built against a newer Cockpit SDK ({builtAgainst}) than this app ({_hostAbstractions}) — it is loaded but may misbehave. Update the app, or reinstall the plugin build made for it.",
            PluginIssueSeverity.Warning);
    }

    private static Version? _ReadBuiltAgainstAbstractions(ICockpitPlugin plugin) =>
        plugin.GetType().Assembly.GetReferencedAssemblies()
            .FirstOrDefault(name => string.Equals(name.Name, "Cockpit.Plugins.Abstractions", StringComparison.Ordinal))?
            .Version;

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
