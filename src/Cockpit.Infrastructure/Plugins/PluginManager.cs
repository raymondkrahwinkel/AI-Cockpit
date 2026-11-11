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

    // AC-2025: starts the UI part of a plugin that just activated and answers why it could not, or null. The desktop sets
    // it, so an update can decide on the outcome; a server has no UI part. It asks `isCurrent` on the UI thread before it
    // touches anything: an attempt that was abandoned meanwhile starts nothing.
    public Func<DiscoveredPlugin, ICockpitPlugin?, Func<bool>, Task<string?>> StartUiPart { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    // AC-2024: raised on the UI thread once the plugin's contributions are undone, for what holds its UI part.
    public event Action<DiscoveredPlugin, ICockpitPlugin?>? Deactivated;

    // Stop begins on the UI thread; completion is awaited without blocking its dispatcher.
    public Func<DiscoveredPlugin, ICockpitPlugin?, Task> StopUiPart { get; set; } = (_, _) => Task.CompletedTask;

    // AC-2025: what the UI holds open for a plugin that an update would close without asking (unsaved work in a
    // workspace): a reason, or null. The desktop sets it; a server has no workspaces.
    public Func<string, Task<string?>> UpdateBlocker { get; set; } = _ => Task.FromResult<string?>(null);

    // AC-2024: the desktop points this at its UI thread; a server has none, and runs the teardown in place.
    public Func<Action, Task> RunOnUiThread { get; set; } = work =>
    {
        work();
        return Task.CompletedTask;
    };

    // AC-1389: Plugin is null for a plugin that is only a UI part, which the desktop's PluginUiManager activates.
    private readonly List<(DiscoveredPlugin Discovered, ICockpitPlugin? Plugin)> _loaded = [];
    private readonly object _lifecycleLock = new();
    private readonly HashSet<(string FolderId, string Sha256)> _hostServiceVersions = [];
    // Container registrations keep the old code alive even when discovery sees a replacement version.
    private readonly HashSet<string> _containerPlugins = new(StringComparer.Ordinal);

    public bool RequiresRestart(string folderId, PluginManifest manifest, string sha256)
    {
        lock (_lifecycleLock)
        {
            return manifest.RequiresRestart || _containerPlugins.Contains(folderId) || _hostServiceVersions.Contains((folderId, sha256));
        }
    }

    private bool _RequiresRestart(DiscoveredPlugin plugin) => RequiresRestart(plugin.FolderId, plugin.Manifest, plugin.Sha256);
    private Func<DiscoveredPlugin, ICockpitPlugin?>? _activate;
    private Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost>? _hostFor;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    // AC-2024: a Dispose that hangs costs the plugin its cleanup, not the operator the Options screen.
    private static readonly TimeSpan _BackendDisposeGrace = TimeSpan.FromSeconds(10);

    // Both directions: a plugin going up or down refuses the other until it has settled.
    private readonly HashSet<string> _activating = new(StringComparer.Ordinal);
    // AC-2025: the running activation per folder; one that was abandoned (it hung) is no longer the current one.
    private readonly Dictionary<string, object> _attempts = new(StringComparer.Ordinal);
    // AC-2023: the activator's non-collectible ALC survives a refusal, so retrying requires a restart. Per bytes
    // (AC-2025): a version that failed does not count against the next one, nor against the one it replaced.
    private readonly Dictionary<string, (string Sha256, string Reason)> _restartReasons = new(StringComparer.Ordinal);

    // AC-2025: the bytes each folder loaded this run. Contexts are non-collectible, so every one but the latest stays in memory.
    private readonly Dictionary<string, HashSet<string>> _loadedBytes = new(StringComparer.Ordinal);

    public int RetiredLoadContexts
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _loadedBytes.Values.Sum(versions => versions.Count - 1);
            }
        }
    }

    private void _NoteLoaded(DiscoveredPlugin discovered)
    {
        lock (_lifecycleLock)
        {
            if (!_loadedBytes.TryGetValue(discovered.FolderId, out var versions))
            {
                _loadedBytes[discovered.FolderId] = versions = [];
            }

            versions.Add(discovered.Sha256);
        }
    }

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
                _NoteLoaded(candidate);
                _loaded.Add((candidate, null));
                continue;
            }

            ICockpitPlugin? plugin;
            try
            {
                _NoteLoaded(candidate);
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
                var configured = new ServiceCollection();
                plugin.ConfigureServices(configured);
                if (configured.Count > 0)
                {
                    _containerPlugins.Add(candidate.FolderId);
                    _hostServiceVersions.Add((candidate.FolderId, candidate.Sha256));
                }

                foreach (var descriptor in configured)
                {
                    services.Add(descriptor);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} threw during ConfigureServices; skipping it.", candidate.FolderId);
                diagnostics.Record(candidate.FolderId, candidate.Manifest.Name, "configure", _Reason(exception));
                _DisposeQuietly(candidate, plugin);
                continue;
            }

            // Before Initialize, as the DI singleton it replaced was: the first-start sign-in (Depot) names its server then.
            if (plugin is IPluginMcpProvider provider)
            {
                McpProviders.Register(provider);
            }

            _loaded.Add((candidate with { RequiresRestart = _RequiresRestart(candidate) }, plugin));
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

    public async Task<bool> WaitForInitializationAsync(CancellationToken cancellationToken = default, bool allowBeforeStart = false)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (SafeMode || (allowBeforeStart && _hostFor is null && !_initialized.Task.IsCompleted))
            {
                return false;
            }
        }

        await _initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Live changes must wait for the frontend too, or they can miss UI activation and teardown.
    public void Initialize(Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor, Action? initializeFrontend = null)
    {
        IReadOnlyList<(DiscoveredPlugin Discovered, ICockpitPlugin? Plugin)> parts;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostFor = hostFor;
            parts = [.. _loaded];
        }

        try
        {
            foreach (var (discovered, plugin) in parts)
            {
                if (plugin is not null)
                {
                    _InitializeOne(discovered, plugin, hostFor);
                }
            }

            initializeFrontend?.Invoke();
            _initialized.TrySetResult();
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
            throw;
        }
    }

    private string? _InitializeOne(
        DiscoveredPlugin discovered,
        ICockpitPlugin plugin,
        Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor,
        object? attempt = null)
    {
        try
        {
            SecretKeyHolder.Shared.Declare(discovered.Manifest.SecretKeys);
            var host = hostFor(discovered, plugin);
            // An abandoned attempt that wakes up late must not register anything under the version that runs by then.
            if (attempt is not null && !_IsCurrent(discovered.FolderId, attempt))
            {
                return "The activation was abandoned.";
            }

            if (host is PluginBackendHost backendHost)
            {
                backendHost.IsLiveActivation = attempt is not null;
            }
            if (plugin is IPluginMcpProvider provider)
            {
                McpProviders.Register(provider);
                RuntimeFor(discovered.FolderId).Add(() => McpProviders.Remove(provider));
            }
            plugin.Initialize(host);
            lock (_lifecycleLock)
            {
                if (!_disposed && (attempt is null || _IsCurrent(discovered.FolderId, attempt)))
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
                if (attempt is null || _IsCurrent(discovered.FolderId, attempt))
                {
                    _initializeFailed.Add(discovered);
                    _hosts.Remove(discovered.FolderId);
                }
            }
            var reason = _Reason(exception);
            diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "initialize", reason);
            return reason;
        }
    }

    private const string _HostServicesRequireRestart = "The plugin registers host services and needs a restart to activate.";

    // AC-2025: why ActivateOne would turn these bytes away before it starts anything, or null; asked before the running
    // version is stopped for them.
    public PluginActivationResult? WouldRefuse(DiscoveredPlugin discovered)
    {
        lock (_lifecycleLock)
        {
            if (_LifecycleRefusal() is { } unavailable)
            {
                return unavailable;
            }

            if (_restartReasons.TryGetValue(discovered.FolderId, out var held) && held.Sha256 == discovered.Sha256)
            {
                return new(PluginActivationStatus.NeedsRestart, held.Reason);
            }

            return _RequiresRestart(discovered)
                ? new(PluginActivationStatus.NeedsHostRestart, "The plugin asks for a restart to activate.")
                : null;
        }
    }

    private PluginActivationResult? _LifecycleRefusal() =>
        SafeMode
            ? new(PluginActivationStatus.NeedsRestart, "Cockpit is in safe mode, so no plugin runs until the next normal start.")
            : _disposed || !_initialized.Task.IsCompletedSuccessfully || _activate is null || _hostFor is null
                ? new(PluginActivationStatus.NeedsRestart, "The plugin lifecycle is not available for live activation.")
                : null;

    public PluginActivationResult ActivateOne(DiscoveredPlugin discovered)
    {
        var attempt = new object();
        Func<DiscoveredPlugin, ICockpitPlugin?> activate;
        Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> hostFor;
        lock (_lifecycleLock)
        {
            if (_LifecycleRefusal() is { } unavailable)
            {
                return unavailable;
            }

            if (_restartReasons.TryGetValue(discovered.FolderId, out var held) && held.Sha256 == discovered.Sha256)
            {
                return new(PluginActivationStatus.NeedsRestart, held.Reason);
            }

            if (_RequiresRestart(discovered))
            {
                return new(PluginActivationStatus.NeedsHostRestart, "The plugin asks for a restart to activate.");
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
            _attempts[discovered.FolderId] = attempt;
            activate = _activate ?? throw new InvalidOperationException("The plugin activator is unavailable.");
            hostFor = _hostFor ?? throw new InvalidOperationException("The plugin host factory is unavailable.");
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
                else if (_RequiresRestart(discovered))
                {
                    result = new(PluginActivationStatus.NeedsHostRestart, "The plugin asks for a restart to activate.");
                }
                else if (discovered.Manifest.EntryAssembly is null)
                {
                    // Its UI part loads in a context of its own, which stays in memory like any other.
                    _NoteLoaded(discovered);
                    result = new(PluginActivationStatus.Activated);
                }
                else
                {
                    _NoteLoaded(discovered);
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
                            lock (_lifecycleLock)
                            {
                                _hostServiceVersions.Add((discovered.FolderId, discovered.Sha256));
                            }
                            result = new(PluginActivationStatus.NeedsHostRestart, _HostServicesRequireRestart);
                        }
                        else
                        {
                            _WarnIfBuiltAgainstNewerHost(discovered, plugin);
                            var failure = _InitializeOne(discovered, plugin, hostFor, attempt);
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

        var kept = false;
        lock (_lifecycleLock)
        {
            if (!_IsCurrent(discovered.FolderId, attempt))
            {
                result = new(PluginActivationStatus.NeedsRestart, "The activation was abandoned after it did not finish in time.");
            }
            else
            {
                if (result.Status is PluginActivationStatus.NeedsRestart && result.Reason is { } reason)
                {
                    _restartReasons[discovered.FolderId] = (discovered.Sha256, reason);
                }

                if (_disposed)
                {
                    result = new(PluginActivationStatus.NeedsRestart, "The plugin manager was disposed during activation.");
                }
                else if (result.Status == PluginActivationStatus.Activated || plugin is not null)
                {
                    _loaded.Add((discovered, plugin));
                    kept = true;
                }

                // An activated plugin stays claimed until its UI part has started or failed.
                if (result.Status != PluginActivationStatus.Activated || !kept)
                {
                    _EndAttempt(discovered.FolderId);
                }
            }
        }

        if (!kept)
        {
            // A late finisher of an abandoned attempt, or a disposed manager: the part never joins what runs.
            if (plugin is not null)
            {
                _DisposeQuietly(discovered, plugin);
            }

            return result;
        }

        if (result.Status != PluginActivationStatus.Activated)
        {
            return result;
        }

        // Abandoned between joining and starting its UI: no UI part may start under what runs by now.
        if (!_IsCurrent(discovered.FolderId, attempt))
        {
            return _DropAbandoned(discovered, plugin);
        }

        var uiFailure = _StartUiPart(discovered, plugin, () => _IsCurrent(discovered.FolderId, attempt));
        lock (_lifecycleLock)
        {
            if (!_IsCurrent(discovered.FolderId, attempt))
            {
                // Dropped behind the start in the UI queue, so a UI part that did start goes away again.
                return _DropAbandoned(discovered, plugin);
            }

            _EndAttempt(discovered.FolderId);
            if (uiFailure is not null)
            {
                _restartReasons[discovered.FolderId] = (discovered.Sha256, uiFailure);
            }
        }

        // The backend part stays loaded: the caller decides, and an update takes the whole plugin back down.
        return uiFailure is null ? result : new(PluginActivationStatus.NeedsRestart, uiFailure);
    }

    // An abandoned attempt that had already joined: the rollback may have taken it out, and if not it goes now.
    private PluginActivationResult _DropAbandoned(DiscoveredPlugin discovered, ICockpitPlugin? plugin)
    {
        bool removed;
        lock (_lifecycleLock)
        {
            // Someone deactivating it right now takes it out themselves.
            removed = !_activating.Contains(discovered.FolderId) && _loaded.RemoveAll(entry => entry.Discovered == discovered && ReferenceEquals(entry.Plugin, plugin)) > 0;
        }

        if (removed)
        {
            try
            {
                _StopUiAsync(discovered, plugin).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId}'s abandoned UI part could not be removed.", discovered.FolderId);
            }

            if (plugin is not null)
            {
                _DisposeQuietly(discovered, plugin);
            }
        }

        return new(PluginActivationStatus.NeedsRestart, "The activation was abandoned after it did not finish in time.");
    }

    private string? _StartUiPart(DiscoveredPlugin discovered, ICockpitPlugin? plugin, Func<bool> isCurrent)
    {
        try
        {
            return StartUiPart(discovered, plugin, isCurrent).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId}'s UI part could not be started.", discovered.FolderId);
            return _Reason(exception);
        }
    }

    private bool _IsCurrent(string folderId, object attempt)
    {
        lock (_lifecycleLock)
        {
            return _attempts.TryGetValue(folderId, out var current) && ReferenceEquals(current, attempt);
        }
    }

    private void _EndAttempt(string folderId)
    {
        _activating.Remove(folderId);
        _attempts.Remove(folderId);
    }

    // AC-2025: an activation that did not finish in time. It is no longer the current one, so what it registers late is
    // undone as it arrives, its part is disposed instead of joining what runs, and the folder is free for the version
    // that comes back. Without this the late part would run under whichever version the pin names by then.
    public async Task AbandonActivationAsync(DiscoveredPlugin discovered, string reason)
    {
        PluginRuntime? runtime;
        lock (_lifecycleLock)
        {
            if (!_attempts.Remove(discovered.FolderId))
            {
                return;
            }

            _activating.Remove(discovered.FolderId);
            _restartReasons[discovered.FolderId] = (discovered.Sha256, reason);
            runtime = _runtimes.Remove(discovered.FolderId, out var held) ? held : null;
        }

        if (runtime is not null)
        {
            await RunOnUiThread(runtime.Dispose).ConfigureAwait(false);
        }
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
            if (_RequiresRestart(discovered))
            {
                return new(PluginActivationStatus.NeedsHostRestart, "The plugin needs a restart to deactivate.");
            }
            _activating.Add(folderId);
        }

        try
        {
            await _StopUiAsync(discovered, plugin).ConfigureAwait(false);
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
            string reason = _Reason(exception);
            lock (_lifecycleLock)
            {
                _restartReasons[folderId] = (discovered.Sha256, reason);
            }
            return new(PluginActivationStatus.NeedsRestart, reason);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _activating.Remove(folderId);
            }
        }
    }

    private async Task _StopUiAsync(DiscoveredPlugin discovered, ICockpitPlugin? plugin)
    {
        Task stopped = Task.CompletedTask;
        await RunOnUiThread(() =>
        {
            RuntimeFor(discovered.FolderId).Dispose();
            stopped = StopUiPart(discovered, plugin);
            _NotifyDeactivated(discovered, plugin);
        }).ConfigureAwait(false);
        await stopped.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private void _NotifyDeactivated(DiscoveredPlugin discovered, ICockpitPlugin? plugin)
    {
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
            _initialized.TrySetCanceled();
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
