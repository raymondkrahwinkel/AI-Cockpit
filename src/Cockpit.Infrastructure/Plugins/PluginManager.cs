using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Capabilities;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Infrastructure.Plugins;

// The two-phase plugin lifecycle (#14): Phase 1 configures each plugin before the container is built, Phase 2
// wires contribution points once it exists; a throwing plugin is logged and skipped. AC-1392: the backend half
// only; the UI parts are the desktop's PluginUiManager, so no window type is named here.
public sealed class PluginManager(
    ILogger<PluginManager> logger,
    PluginDiagnostics diagnostics,
    bool safeMode = false,
    Version? hostAbstractionsVersion = null,
    Func<ICockpitPlugin, Version?>? builtAgainstResolver = null) : IDisposable
{
    // AC-478: reachable even when a UI plugin is crashing on load. The value lives in Core, where the frontend reads it.
    public const string SafeModeArgument = CockpitBuild.SafeModeArgument;

    // Whether this run skips the load phase entirely (AC-478) — read by the host to show the safe-mode marker.
    public bool SafeMode { get; } = safeMode;

    public PluginMcpProviderRegistry McpProviders { get; } = new();

    public event Action<DiscoveredPlugin, ICockpitPlugin?>? Activated;

    // AC-2024: raised on the UI thread once the plugin's contributions are undone, for what holds its UI part.
    public event Action<DiscoveredPlugin, ICockpitPlugin?>? Deactivated;

    // AC-2024: the desktop points this at its UI thread; a server has none, and runs the teardown in place.
    public Func<Action, Task> RunOnUiThread { get; set; } = work =>
    {
        work();
        return Task.CompletedTask;
    };

    // AC-1389: Plugin is null for a plugin that is only a UI part, which the desktop's PluginUiManager activates.
    private readonly List<(DiscoveredPlugin Discovered, ICockpitPlugin? Plugin)> _loaded = [];
    private readonly object _lifecycleLock = new();
    private Func<DiscoveredPlugin, ICockpitPlugin?>? _activate;
    private Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost>? _hostFor;
    private bool _disposed;
    // AC-2024: a Dispose that hangs costs the plugin its cleanup, not the operator the Options screen.
    private static readonly TimeSpan _BackendDisposeGrace = TimeSpan.FromSeconds(10);

    // Both directions: a plugin going up or down refuses the other until it has settled.
    private readonly HashSet<string> _activating = new(StringComparer.Ordinal);
    // AC-2023: the activator's non-collectible ALC survives a refusal, so retrying requires a restart.
    private readonly Dictionary<string, string> _restartReasons = new(StringComparer.Ordinal);

    // AC-1389: a plugin whose backend part threw in Initialize gets no UI part, which would only meet unknown actions.
    private readonly HashSet<DiscoveredPlugin> _initializeFailed = [];

    // AC-1571: the host each backend part was given, so the admin API writes through the storage that plugin reads.
    private readonly Dictionary<string, ICockpitHost> _hosts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, PluginRuntime> _runtimes = new(StringComparer.Ordinal);

    // Both the host's abstractions version and how to read a plugin's are seams so a test can drive the
    // drift check without a real assembly; defaults read the host's own Abstractions and the plugin's
    // compile-time assembly reference (which no manifest can misstate).
    private readonly Version _hostAbstractions =
        hostAbstractionsVersion ?? typeof(AbstractionsContract).Assembly.GetName().Version ?? new Version(0, 0);

    private readonly Func<ICockpitPlugin, Version?> _builtAgainst = builtAgainstResolver ?? _ReadBuiltAgainstAbstractions;

    // The plugins that actually loaded — their manifests, for the host to read what they declared (e.g. which storage keys hold a credential).
    public IReadOnlyList<DiscoveredPlugin> Loaded => [.. LoadedParts.Select(entry => entry.Discovered)];

    // Every loaded plugin with its backend part, null for a plugin that is only a UI part.
    public IReadOnlyList<(DiscoveredPlugin Discovered, ICockpitPlugin? Plugin)> LoadedParts
    {
        get
        {
            lock (_lifecycleLock)
            {
                return [.. _loaded];
            }
        }
    }

    // AC-1389: a plugin whose Initialize threw gets no UI part.
    public bool InitializeFailed(DiscoveredPlugin discovered)
    {
        lock (_lifecycleLock)
        {
            return _initializeFailed.Contains(discovered);
        }
    }

    // AC-1571: the host of a plugin whose backend part initialized, or null.
    public ICockpitHost? HostOf(string folderId)
    {
        lock (_lifecycleLock)
        {
            return _hosts.GetValueOrDefault(folderId);
        }
    }

    // AC-2024: one per plugin, so its hosts record into the same place; a closed one gives way to a fresh activation.
    public PluginRuntime RuntimeFor(string folderId)
    {
        lock (_lifecycleLock)
        {
            if (!_runtimes.TryGetValue(folderId, out var runtime) || runtime.IsDisposed)
            {
                runtime = _runtimes[folderId] = new PluginRuntime(folderId, logger);
            }

            return runtime;
        }
    }

    // Phase 1, before `BuildServiceProvider`: instantiate each `Load`-decided plugin and run its
    // `ConfigureServices` against the still-open `services`; failures are skipped (and disposed if created).
    public void LoadAndConfigure(
        IReadOnlyList<DiscoveredPlugin> discovered,
        IServiceCollection services,
        Func<DiscoveredPlugin, ICockpitPlugin?> activate)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activate = activate;
            _LoadAndConfigure(discovered, services, activate);
        }
    }

    private void _LoadAndConfigure(
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

            // AC-1389: a plugin that is only a UI part has nothing to configure; InitializeUi activates it.
            if (candidate.Manifest.EntryAssembly is null)
            {
                _loaded.Add((candidate, null));
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
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "load", _Reason(exception));
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
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "configure", _Reason(exception));
                _DisposeQuietly(candidate, plugin);
                continue;
            }

            _loaded.Add((candidate, plugin));
            _WarnIfBuiltAgainstNewerHost(candidate, plugin);
        }
    }

    // AC-2025: hashes the copy again (a copy made earlier is not trusted on its name) and swaps in the manifest it holds.
    private static string? _VerifyCopy(ref DiscoveredPlugin discovered)
    {
        try
        {
            var shadow = PluginShadow.GetOrPrepare(discovered, fresh: true);
            if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(shadow.Folder, "plugin.json")), out var manifest, out _) || manifest is null)
            {
                return "The plugin manifest could not be read from the verified copy.";
            }

            discovered = discovered with { Manifest = manifest };
            return null;
        }
        catch (PluginIntegrityException exception)
        {
            return exception.Message;
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

            case PluginLoadDecision.UnknownCapability:
                var unknown = string.Join(", ", candidate.Manifest.Capabilities.Where(id => CapabilityCatalog.Find(id) is null));
                logger.LogWarning(
                    "Plugin {PluginId} asks for a capability this cockpit does not know ({Capabilities}) and was not loaded.",
                    candidate.FolderId, unknown);
                diagnostics.Record(
                    candidate.FolderId, candidate.Manifest.Name, "load",
                    $"This plugin asks for a capability this app does not know ({unknown}) — update the app or the plugin.",
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

    // AC-1403: the runtime wraps a load context's refusal in a FileLoadException; the reason is the refusal's own.
    private static string _Reason(Exception exception) =>
        exception is FileLoadException { InnerException: InvalidOperationException refusal } ? refusal.Message : exception.Message;

    private static Version? _ReadBuiltAgainstAbstractions(ICockpitPlugin plugin) =>
        plugin.GetType().Assembly.GetReferencedAssemblies()
            .FirstOrDefault(name => string.Equals(name.Name, "Cockpit.Plugins.Abstractions", StringComparison.Ordinal))?
            .Version;

    // Phase 2, after the container exists: give each loaded plugin the host `hostFor` built for it so
    // it can register contribution points (a throwing plugin is logged and skipped). `hostFor` also gets the
    // loaded `ICockpitPlugin` (AC-499), which MCP tool-call resolution scopes a fallback to.
    public void Initialize(Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor)
    {
        IReadOnlyList<(DiscoveredPlugin Discovered, ICockpitPlugin? Plugin)> parts;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostFor = hostFor;
            parts = [.. _loaded];
        }

        foreach (var (discovered, plugin) in parts)
        {
            if (plugin is not null)
            {
                _InitializeOne(discovered, plugin, hostFor);
            }
        }
    }

    private string? _InitializeOne(
        DiscoveredPlugin discovered,
        ICockpitPlugin plugin,
        Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor,
        bool liveActivation = false)
    {
        try
        {
            SecretKeyHolder.Shared.Declare(discovered.Manifest.SecretKeys);
            var host = hostFor(discovered, plugin);
            if (host is PluginBackendHost backendHost)
            {
                backendHost.IsLiveActivation = liveActivation;
            }
            if (plugin is IPluginMcpProvider provider)
            {
                McpProviders.Register(provider);
                RuntimeFor(discovered.FolderId).Add(() => McpProviders.Remove(provider));
            }
            plugin.Initialize(host);
            lock (_lifecycleLock)
            {
                if (!_disposed)
                {
                    _hosts[discovered.FolderId] = host;
                }
            }
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId} threw during Initialize; its contributions are skipped.", discovered.FolderId);
            if (plugin is IPluginMcpProvider provider)
            {
                McpProviders.Remove(provider);
            }
            lock (_lifecycleLock)
            {
                _initializeFailed.Add(discovered);
                _hosts.Remove(discovered.FolderId);
            }
            var reason = _Reason(exception);
            diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "initialize", reason);
            return reason;
        }
    }

    public PluginActivationResult ActivateOne(DiscoveredPlugin discovered)
    {
        Func<DiscoveredPlugin, ICockpitPlugin?> activate;
        Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor;
        lock (_lifecycleLock)
        {
            if (_disposed || SafeMode || _activate is null || _hostFor is null)
            {
                return new(PluginActivationStatus.NeedsRestart, "The plugin lifecycle is not available for live activation.");
            }

            if (_restartReasons.TryGetValue(discovered.FolderId, out var reason))
            {
                return new(PluginActivationStatus.NeedsRestart, reason);
            }

            if (discovered.Manifest.RequiresRestart)
            {
                return new(PluginActivationStatus.NeedsRestart, "The plugin asks for a restart to activate.");
            }

            if (_activating.Contains(discovered.FolderId))
            {
                return new(PluginActivationStatus.Busy, "The plugin is being activated.");
            }

            var existing = _loaded.FirstOrDefault(entry => entry.Discovered.FolderId == discovered.FolderId);
            if (existing.Discovered is not null)
            {
                return _initializeFailed.Contains(existing.Discovered)
                    ? new(PluginActivationStatus.NeedsRestart, "The plugin previously failed to initialize.")
                    : new(PluginActivationStatus.AlreadyActive);
            }

            _activating.Add(discovered.FolderId);
            activate = _activate;
            hostFor = _hostFor;
        }

        ICockpitPlugin? plugin = null;
        PluginActivationResult result;
        var phase = "load";
        try
        {
            if (discovered.Decision != PluginLoadDecision.Load)
            {
                _NoteSkipped(discovered);
                result = new(PluginActivationStatus.Refused, $"The plugin load decision is {discovered.Decision}.");
            }
            else
            {
                // AC-2023: display discovery may reuse file hashes; loading must read every byte again.
                // AC-2025: the bytes are read from the copy that is loaded, so what is approved is what runs.
                // The manifest is read from that copy too: a re-enable must not trust what discovery read from the original.
                var changed = _VerifyCopy(ref discovered);
                if (changed is not null)
                {
                    logger.LogWarning("Plugin {PluginId} failed to load; skipping it. {Reason}", discovered.FolderId, changed);
                    diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "load", changed);
                    result = new(PluginActivationStatus.Refused, changed);
                }
                else if (discovered.Manifest.RequiresRestart)
                {
                    result = new(PluginActivationStatus.NeedsRestart, "The plugin asks for a restart to activate.");
                }
                else if (discovered.Manifest.EntryAssembly is null)
                {
                    result = new(PluginActivationStatus.Activated);
                }
                else
                {
                    plugin = activate(discovered);
                    if (plugin is null)
                    {
                        logger.LogWarning("Plugin {PluginId} did not yield an ICockpitPlugin; skipping it.", discovered.FolderId);
                        diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "load", "The plugin did not yield an ICockpitPlugin.");
                        result = new(PluginActivationStatus.NeedsRestart, "The plugin did not yield an ICockpitPlugin.");
                    }
                    else
                    {
                        phase = "configure";
                        var services = new ServiceCollection();
                        plugin.ConfigureServices(services);
                        if (services.Count > 0)
                        {
                            _DisposeQuietly(discovered, plugin);
                            plugin = null;
                            result = new(PluginActivationStatus.NeedsRestart, "ConfigureServices registered services that require rebuilding the host container.");
                        }
                        else
                        {
                            _WarnIfBuiltAgainstNewerHost(discovered, plugin);
                            var failure = _InitializeOne(discovered, plugin, hostFor, liveActivation: true);
                            result = failure is null
                                ? new(PluginActivationStatus.Activated)
                                : new(PluginActivationStatus.NeedsRestart, failure);
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (plugin is not null)
            {
                _DisposeQuietly(discovered, plugin);
                plugin = null;
            }

            var reason = _Reason(exception);
            logger.LogWarning(exception, "Plugin {PluginId} failed to load; skipping it.", discovered.FolderId);
            diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, phase, reason);
            result = new(PluginActivationStatus.NeedsRestart, reason);
        }

        lock (_lifecycleLock)
        {
            _activating.Remove(discovered.FolderId);
            if (result.Status == PluginActivationStatus.NeedsRestart && result.Reason is { } reason)
            {
                _restartReasons[discovered.FolderId] = reason;
            }

            if (!_disposed)
            {
                if (result.Status == PluginActivationStatus.Activated || plugin is not null)
                {
                    _loaded.Add((discovered, plugin));
                }

                if (result.Status != PluginActivationStatus.Activated)
                {
                    return result;
                }
            }
            else
            {
                result = new(PluginActivationStatus.NeedsRestart, "The plugin manager was disposed during activation.");
            }
        }

        if (result.Status == PluginActivationStatus.Activated)
        {
            try
            {
                Activated?.Invoke(discovered, plugin);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId}'s activation subscriber failed.", discovered.FolderId);
            }
            return result;
        }

        if (plugin is not null)
        {
            _DisposeQuietly(discovered, plugin);
        }

        return result;
    }

    // AC-2024: no lock across the hop, since the Changed subscribers that run in it read LoadedParts. The backend part
    // leaves off the UI thread: it has to work on a server, so it may block on a worker that marshals back to it.
    public async Task<PluginActivationResult> DeactivateAsync(string folderId)
    {
        DiscoveredPlugin discovered;
        ICockpitPlugin? plugin;
        lock (_lifecycleLock)
        {
            if (_activating.Contains(folderId))
            {
                return new(PluginActivationStatus.Busy, "The plugin is being activated or deactivated.");
            }

            var loaded = _loaded.FirstOrDefault(entry => entry.Discovered.FolderId == folderId);
            if (_disposed || loaded.Discovered is null)
            {
                return new(PluginActivationStatus.NotActive);
            }

            (discovered, plugin) = loaded;
            _activating.Add(folderId);
        }

        try
        {
            await RunOnUiThread(() => _UndoContributions(discovered, plugin)).ConfigureAwait(false);
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return new(PluginActivationStatus.NotActive);
                }

                _loaded.RemoveAll(entry => entry.Discovered.FolderId == folderId);
                _hosts.Remove(folderId);
                _initializeFailed.Remove(discovered);
                _runtimes.Remove(folderId);
            }

            if (plugin is not null)
            {
                await _DisposeOffUiThreadAsync(discovered, plugin).ConfigureAwait(false);
            }

            return new(PluginActivationStatus.Deactivated);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId} could not be deactivated; a restart unloads it.", folderId);
            return new(PluginActivationStatus.NeedsRestart, _Reason(exception));
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _activating.Remove(folderId);
            }
        }
    }

    private void _UndoContributions(DiscoveredPlugin discovered, ICockpitPlugin? plugin)
    {
        RuntimeFor(discovered.FolderId).Dispose();
        try
        {
            Deactivated?.Invoke(discovered, plugin);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId}'s deactivation subscriber failed.", discovered.FolderId);
        }
    }

    private async Task _DisposeOffUiThreadAsync(DiscoveredPlugin discovered, ICockpitPlugin plugin)
    {
        try
        {
            await Task.Run(() => _DisposeQuietly(discovered, plugin)).WaitAsync(_BackendDisposeGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Plugin {PluginId} did not finish disposing in time; it is deactivated regardless.", discovered.FolderId);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
            foreach (var (discovered, plugin) in LoadedParts)
            {
                if (plugin is not null)
                {
                    _DisposeQuietly(discovered, plugin);
                }
            }

            _loaded.Clear();
            _hosts.Clear();
            _initializeFailed.Clear();
        }
    }

    private void _DisposeQuietly(DiscoveredPlugin discovered, ICockpitPlugin part)
    {
        if (part is IPluginMcpProvider provider)
        {
            McpProviders.Remove(provider);
        }
        try
        {
            part.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId} threw while disposing.", discovered.FolderId);
        }
    }
}
