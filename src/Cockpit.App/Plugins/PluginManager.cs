using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// Drives the two-phase plugin lifecycle across the app's DI bootstrap (#14): Phase 1 instantiates
// and configures each loaded plugin before the container is built, Phase 2 wires contribution points
// once the container and UI exist. One plugin that throws is logged and skipped, never taking down the rest.
public sealed class PluginManager(
    ILogger<PluginManager> logger,
    PluginDiagnostics diagnostics,
    bool safeMode = false,
    Version? hostAbstractionsVersion = null,
    Func<ICockpitPlugin, Version?>? builtAgainstResolver = null) : IDisposable
{
    // AC-478: starts the cockpit with no plugins loaded, reachable even when a UI plugin is crashing on
    // load; a restart strips it again so it is always a one-shot recovery, never left on by hand.
    public const string SafeModeArgument = "--safe-mode";

    // Whether this run skips the load phase entirely (AC-478) — read by the host to show the safe-mode marker.
    public bool SafeMode { get; } = safeMode;

    private readonly List<(DiscoveredPlugin Discovered, ICockpitPlugin Plugin)> _loaded = [];

    // Both the host's abstractions version and how to read a plugin's are seams so a test can drive the
    // drift check without a real assembly; defaults read the host's own Abstractions and the plugin's
    // compile-time assembly reference (which no manifest can misstate).
    private readonly Version _hostAbstractions =
        hostAbstractionsVersion ?? typeof(AbstractionsContract).Assembly.GetName().Version ?? new Version(0, 0);

    private readonly Func<ICockpitPlugin, Version?> _builtAgainst = builtAgainstResolver ?? _ReadBuiltAgainstAbstractions;

    // The plugins that actually loaded — their manifests, for the host to read what they declared (e.g. which storage keys hold a credential).
    public IReadOnlyList<DiscoveredPlugin> Loaded => [.. _loaded.Select(entry => entry.Discovered)];

    // AC-1033: the assembly each loaded plugin came out of, which is where the knowledge base reads its
    // embedded documentation — the same artefact as the code, so the two cannot drift apart.
    public IReadOnlyList<(DiscoveredPlugin Discovered, System.Reflection.Assembly Assembly)> LoadedWithAssemblies =>
        [.. _loaded.Select(entry => (entry.Discovered, entry.Plugin.GetType().Assembly))];

    // Phase 1, before `BuildServiceProvider`: instantiate each `Load`-decided plugin and run its
    // `ConfigureServices` against the still-open `services`; failures are skipped (and disposed if created).
    public void LoadAndConfigure(
        IReadOnlyList<DiscoveredPlugin> discovered,
        IServiceCollection services,
        Func<DiscoveredPlugin, ICockpitPlugin?> activate)
    {
        // AC-478: safe mode skips the load phase outright, including the breadcrumb below — the point is a
        // host that never instantiates a plugin. `discovered` is still computed by the caller, just not walked.
        if (SafeMode)
        {
            logger.LogInformation(
                "Safe mode ({SafeModeArgument}): skipping the plugin load phase; {Count} discovered plugin(s) were not instantiated.",
                SafeModeArgument, discovered.Count);
            return;
        }

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

    // A skipped plugin discovery otherwise leaves no trace, so a silently vanished provider becomes an
    // unexplained "no such provider" downstream — this is that breadcrumb. Refused/awaiting-consent decisions
    // are also recorded for the UI (AC-208); disabled is log-only since the manager already shows that state.
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

    // A plugin compiled against a newer Abstractions SDK than this app ships may call a member the host copy
    // lacks; it stays loaded (usually still works) but this warns explicitly instead of an unexplained throw later.
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

    // Phase 2, after the container and UI exist: give each loaded plugin the host `hostFor` built for it so
    // it can register contribution points (a throwing plugin is logged and skipped). `hostFor` also gets the
    // loaded `ICockpitPlugin` (AC-499), which MCP tool-call resolution scopes a fallback to.
    public void Initialize(Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor)
    {
        foreach (var (discovered, plugin) in _loaded)
        {
            try
            {
                plugin.Initialize(hostFor(discovered, plugin));
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
