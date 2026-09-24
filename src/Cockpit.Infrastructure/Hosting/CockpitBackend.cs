using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Hosting;

// AC-1381: the backend's startup, for the desktop and for a process with no App. It comes in steps because the desktop
// runs its single-instance guard and its plugin pass in between; each step keeps the moment it had in `Program.cs`.
public sealed class CockpitBackend
{
    private Task? _reconcile;
    private bool _pluginSettingsSeeded;
    private bool _pluginsInitialized;

    private CockpitBackend(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    // Before the single-instance guard, and before anything can spawn: scrub inherited session identity (AC-42),
    // terminal identity (#58) and credentials. AC-1351: the bootstrap connect key is one of those credentials, so it
    // is taken for the node's door first and never reaches a session.
    public static void ScrubInheritedEnvironment()
    {
        ConnectKeyBootstrapEnvironment.Capture();

        var markers = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && TtyEnvironment.IsHostControlled(key))
            {
                markers.Add(key);
            }
        }

        foreach (var key in markers)
        {
            // Managed + native (libc) both: Skia and a spawned child read the native environ via getenv, so a
            // managed-only removal would leave the stripped variable leaking through.
            ProcessEnvironment.Remove(key);
        }

        // A terminal-specific TERM (e.g. xterm-ghostty) is what the SvcSystems/Skia render stack keys off,
        // drawing every line underlined; normalise anything that is not already the generic value.
        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) && !string.Equals(term, TtyEnvironment.TermValue, StringComparison.OrdinalIgnoreCase))
        {
            ProcessEnvironment.Assign("TERM", TtyEnvironment.TermValue);
        }
    }

    // Once this process is the cockpit. The bare AI_COCKPIT marker reaches every nested agent regardless of spawn path
    // (#45 D4); MSBuild workers otherwise outlive their build. Then, before any state access, restrict legacy
    // world-readable files and remove crash-left --mcp-config files containing bearer headers.
    public static void MarkProcess()
    {
        ProcessEnvironment.Assign("AI_COCKPIT", "1");
        ProcessEnvironment.Assign("MSBUILDDISABLENODEREUSE", "1");
        CredentialFileHousekeeping.Run();
    }

    // A GUI or AppImage launch hands this process a PATH without the user's bin directories (AC-19), and a session not
    // resumed after a crash still has its processes (AC-1093). Both are dealt with before any session of this run.
    public static void RepairProcess(ILoggerFactory loggerFactory)
    {
        StartupPathRepair.Run(loggerFactory.CreateLogger(typeof(StartupPathRepair)));
        StaleSessionProcessSweep.Run(loggerFactory.CreateLogger(typeof(StaleSessionProcessSweep)));
    }

    // Core, Infrastructure and the no-frontend defaults, then whatever `frontend` adds; the last registration wins,
    // so a frontend's own seams replace the defaults. AC-1392: then plugin phase 1, before the container is built,
    // so a plugin's ConfigureServices registers on top of all of it (#14).
    public static CockpitBackend Build(ILoggerFactory loggerFactory, Action<IServiceCollection>? frontend = null, PluginStartup plugins = PluginStartup.None)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
        _AddNoFrontendDefaults(services);
        frontend?.Invoke(services);
        if (plugins != PluginStartup.None)
        {
            _LoadPlugins(services, loggerFactory, plugins == PluginStartup.SafeMode);
        }

        CockpitBackend? built = null;
        services.AddSingleton(_ => built ?? throw new InvalidOperationException("The backend is resolved from its own container only once built."));
        built = new CockpitBackend(services.BuildServiceProvider());
        return built;
    }

    // The hosted services first: the MCP permission server must run before the first session spawns a CLI. Then the
    // worktree reconcile and state compaction, against one saved-pane roster so restorable panes are not treated as
    // orphans (AC-85/AC-409/AC-410), registered with the gate before it starts so a restore waits for it.
    public void Start()
    {
        foreach (var service in Services.GetServices<IHostedService>())
        {
            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        var reconcile = ReconcileWorktreesAndCompactStateAsync(
            Services.GetRequiredService<IWorktreeManager>(),
            Services.GetRequiredService<ISessionStateStore>(),
            Services.GetRequiredService<IWorkspaceSettingsStore>());
        Services.GetRequiredService<IWorktreeReconcileGate>().SignalStarted(reconcile);
        _reconcile = reconcile;

        // Reconcile the repository-clone registry too (AC-90): forget any clone whose folder disappeared since last
        // run so the reuse check and the list reflect what is on disk. Fire-and-forget, and it only drops registry
        // entries — a clone folder that still exists is never deleted, because it may hold uncommitted work.
        _ = Services.GetRequiredService<IRepositoryCloneManager>().ReconcileAsync();
    }

    // AC-1392: plugin phase 2's first backend step, where the desktop had it in App. The declared secret keys before
    // any settings are read, or ciphertext could reach a plugin; the terminal and shell switches before any session can
    // start. True when a key was declared, which can turn a stored value into a credential.
    public bool SeedPluginSettings()
    {
        if (Services.GetService<PluginManager>() is not { } plugins || _pluginSettingsSeeded)
        {
            return false;
        }

        _pluginSettingsSeeded = true;

        // ponytail: blocks on its store loads, as the whole plugin startup does (_LoadPlugins, PluginStorage.ForPlugin):
        // synchronous by design, before the UI or on its thread with loads that never capture it. Ceiling: an async
        // startup that awaits these, once a frontend needs one.
        var declared = Services.GetRequiredService<IPluginSecretFieldStore>().LoadAsync().GetAwaiter().GetResult()
            .Concat(plugins.Loaded.SelectMany(discovered => discovered.Manifest.SecretKeys))
            .ToList();
        if (declared.Count > 0)
        {
            SecretKeyHolder.Shared.Declare(declared);
        }

        // AC-34/AC-1066: a session that launches before the operator ever opens Options still gets the saved choice.
        Services.GetRequiredService<ITerminalAccessSwitch>().Enabled =
            Services.GetRequiredService<ITerminalAccessSettingsStore>().LoadAsync().GetAwaiter().GetResult().Enabled;
        Services.GetRequiredService<IShellAccessSwitch>().Enabled =
            Services.GetRequiredService<IShellAccessSettingsStore>().LoadAsync().GetAwaiter().GetResult().Enabled;

        return declared.Count > 0;
    }

    // AC-1392: plugin phase 2's backend half — before the planners and the restore. Seeds the settings above if the
    // frontend has not, then every plugin's Initialize with the host `hostFor` builds, a windowless one by default.
    public void InitializePlugins(Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost>? hostFor = null)
    {
        if (Services.GetService<PluginManager>() is not { } plugins)
        {
            return;
        }

        SeedPluginSettings();
        plugins.Initialize(hostFor ?? _BackendHostFor());
        _pluginsInitialized = true;
    }

    // AC-1380's planners. Never before `Start`: the worktree planner's crash net assumes the startup sweep has run, and
    // the rest read a registry the reconcile's roster decides. The Depot watcher polls only what its `BoundProjects`
    // names, which a frontend that owns a project list sets before this.
    public void StartPlanners()
    {
        if (_reconcile is null)
        {
            throw new InvalidOperationException("The planners start after the startup reconcile, and Start has not run it yet.");
        }

        // AC-1392: nor before the plugins' Initialize, which registers the providers and endpoints a planner may reach.
        if (Services.GetService<PluginManager>() is not null && !_pluginsInitialized)
        {
            throw new InvalidOperationException("The planners start after the plugins' Initialize, and InitializePlugins has not run it yet.");
        }

        // AC-1380: the session registry reads are thread-safe from any thread (AC-1373), and every planner below
        // ticks on a threadpool thread.
        var sessionRegistry = Services.GetRequiredService<ISessionRegistry>();

        // AC-634: watch the branches the sessions are on for a failing CI check. The watch set is the live sessions
        // rather than a configured list, so a worktree opened later is followed without anyone saying so.
        if (Services.GetService<CiWatcher>() is { } ciWatcher)
        {
            ciWatcher.Watching = () =>
            [
                .. sessionRegistry.All
                    .Where(session => !string.IsNullOrWhiteSpace(session.WorkingDirectory))
                    .Select(session => new WatchedCheckout(session.PaneId, session.Title, session.WorkingDirectory ?? string.Empty)),
            ];
            ciWatcher.Start();
        }

        // AC-640: the same shape one layer along, for the sessions the assistant armed a watch on with
        // `watch_session`. Started with nothing watched, unlike the CI one: it only ever follows what it was asked to.
        if (Services.GetService<SessionWatcher>() is { } sessionWatcher)
        {
            sessionWatcher.Probe = SessionWatcher.ProbeOf(sessionRegistry);
            sessionWatcher.Start();
        }

        // AC-656: and give every pane a turn as soon as its own inbox has mail, instead of leaving it for that
        // pane's next turn or tool call to notice. Unlike SessionWatcher this needs nothing armed — every live pane
        // is checked, the assistant included (`All` does not carry it; `cockpit-agents` reaches it anyway).
        if (Services.GetService<InboxWakeScheduler>() is { } inboxWakeScheduler)
        {
            inboxWakeScheduler.Panes = () =>
            [
                AssistantIdentity.PaneId,
                .. sessionRegistry.All.Select(session => session.PaneId),
            ];
            inboxWakeScheduler.Start();
        }

        // AC-643: and keep the worktree crash net ticking after the startup sweep, against the sessions that are
        // live at that moment — a worktree whose owner crashed at noon is reconciled then, not at the next restart.
        if (Services.GetService<WorktreeReconciler>() is { } worktreeReconciler)
        {
            // AC-654: asked of the liveness registry rather than the grid, because a pane-only answer misses the
            // sessions that run without one (a delegated task, AC-106) and sweeps the worktree out from under them.
            var liveSessions = Services.GetService<ILiveSessionRegistry>();
            worktreeReconciler.LiveSessionIds = liveSessions is { } registry
                ? () => registry.LiveSessionIds
                : () => sessionRegistry.All.Select(session => session.PaneId).ToList();
            worktreeReconciler.Start();
        }

        // AC-894: poll every Depot-bound project's checksum for a change made elsewhere.
        Services.GetService<DepotSyncWatcher>()?.Start();

        // AC-644: the same crash net one layer up, for the claims a session that never closed left standing.
        if (Services.GetService<StaleClaimReaper>() is { } claimReaper)
        {
            claimReaper.LivePaneIds = () =>
            [
                // The assistant, which `All` does not carry, holds claims like anyone else: `cockpit-agents`
                // is AlwaysMounted and reaches it too. Left out, its own claims would be reaped on the first tick.
                AssistantIdentity.PaneId,
                .. sessionRegistry.All.Select(session => session.PaneId),
            ];
            claimReaper.Start();
        }
    }

    // Read the saved AI-pane roster once so worktree reconciliation and state compaction cannot disagree, and
    // compaction can safely drop state for panes that will not be restored (AC-410).
    private static async Task ReconcileWorktreesAndCompactStateAsync(
        IWorktreeManager worktreeManager,
        ISessionStateStore sessionStateStore,
        IWorkspaceSettingsStore workspaceSettingsStore)
    {
        var restorablePaneIds = await SessionRestoreRoster.PaneIdsAsync(workspaceSettingsStore).ConfigureAwait(false);

        // At fresh start, retain roster worktrees for possible restore; outside it, remove clean worktrees, retain
        // dirty ones, and prune stale Git metadata (AC-85).
        await worktreeManager.ReconcileAsync(restorablePaneIds).ConfigureAwait(false);

        // Fold duplicate session-state records left by earlier runs (AC-409), now against the same roster: a pane
        // no longer named in cockpit.json has its state dropped instead of kept forever. Run after the reconcile
        // above so a worktree it just kept for a restorable pane is never the one compaction treats as gone.
        await sessionStateStore.CompactAsync(restorablePaneIds).ConfigureAwait(false);
    }

    // Plugin phase 1 (#14): install what this build ships, discover, and instantiate and configure each plugin that may
    // load; a failure is logged and the backend continues without plugins. Safe mode discovers but loads none (AC-478).
    private static void _LoadPlugins(IServiceCollection services, ILoggerFactory loggerFactory, bool safeMode)
    {
        var diagnostics = new PluginDiagnostics();
        services.AddSingleton(diagnostics);
        var manager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), diagnostics, safeMode);
        try
        {
            // Before discovery, for first-run availability; best-effort, so a failure cannot hide installed plugins.
            _InstallBundledPlugins(loggerFactory);
#if DEBUG
            // Dev inner loop only: replace already-installed first-party plugins with their freshly built bytes.
            _RefreshDevPlugins(loggerFactory);
#endif

            // The one pass that applies a staged update or a marked removal: no plugin is loaded yet.
            var discovered = new PluginBootstrap()
                .ApplyPendingChangesAndDiscoverAsync(AbstractionsContract.Version).GetAwaiter().GetResult();
            manager.LoadAndConfigure(discovered, services, new PluginActivator(loggerFactory.CreateLogger<PluginActivator>()).Activate);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<CockpitBackend>().LogError(exception, "Plugin discovery failed; continuing without plugins.");
        }

        services.AddSingleton(manager);
    }

    private static void _InstallBundledPlugins(ILoggerFactory loggerFactory)
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, BundledPluginInstaller.BundledFolderName);

        try
        {
            var installed = new BundledPluginInstaller(loggerFactory.CreateLogger<BundledPluginInstaller>())
                .InstallAsync(bundledRoot, PluginBootstrap.PluginsRoot)
                .GetAwaiter()
                .GetResult();

            if (installed.Count > 0)
            {
                loggerFactory.CreateLogger<CockpitBackend>().LogInformation(
                    "Installed the plugins shipped with this build: {Plugins}", string.Join(", ", installed));
            }
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<CockpitBackend>().LogWarning(
                exception, "Could not install the bundled plugins; continuing with whatever is already installed.");
        }
    }

#if DEBUG
    // Refreshes already-installed first-party plugins from their freshly built output (see DevPluginInstaller).
    private static void _RefreshDevPlugins(ILoggerFactory loggerFactory)
    {
        try
        {
            var refreshed = new DevPluginInstaller(loggerFactory.CreateLogger<DevPluginInstaller>())
                .InstallAsync(PluginBootstrap.PluginsRoot)
                .GetAwaiter()
                .GetResult();

            if (refreshed.Count > 0)
            {
                loggerFactory.CreateLogger<CockpitBackend>().LogInformation(
                    "Refreshed first-party plugins from the dev build: {Plugins}", string.Join(", ", refreshed));
            }
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<CockpitBackend>().LogWarning(
                exception, "Could not refresh dev plugins; continuing with whatever is already installed.");
        }
    }
#endif

    // The windowless host for every plugin, sharing one observer, one actions surface and one cache file (AC-1294).
    private Func<DiscoveredPlugin, ICockpitPlugin, ICockpitHost> _BackendHostFor()
    {
        var registrationStore = Services.GetRequiredService<IPluginRegistrationStore>();
        var secretFieldStore = Services.GetRequiredService<IPluginSecretFieldStore>();
        var cache = PluginCacheStore.ForStateRoot(Services.GetService<ILogger<PluginCacheStore>>());
        var sessions = new PluginBackendSessionObserver(Services.GetRequiredService<ISessionRegistry>());
        var actions = new PluginBackendActions(
            Services.GetRequiredService<ISessionProfileStore>(),
            Services.GetRequiredService<IDelegationService>(),
            Services.GetService<ISessionLauncher>());
        var diagnostics = Services.GetRequiredService<PluginDiagnostics>();

        return (discovered, plugin) => new PluginBackendHost(
            discovered.FolderId,
            discovered.Manifest.Name,
            Services,
            PluginStorage.ForPlugin(discovered, registrationStore, secretFieldStore),
            sessions,
            actions,
            diagnostics,
            plugin.GetType(),
            cache.CreateFor(discovered.FolderId));
    }

    // AC-1378: the launcher is built over the desks as saved, the first time something asks for it; a desktop that
    // registers its own never reads them here.
    private static void _AddNoFrontendDefaults(IServiceCollection services)
    {
        services.AddSingleton<ISessionLauncher>(provider =>
        {
            var workspaces = provider.GetRequiredService<IWorkspaceSettingsStore>();

            // ponytail: blocks on the desk load; safe while the backend path has no SynchronizationContext to deadlock on.
            // Ceiling: a host that resolves this on a context thread (a UI thread) could hang; then load the desks in Start.
            return new SessionLauncher(
                workspaces.LoadAsync().GetAwaiter().GetResult(),
                workspaces,
                provider.GetRequiredService<IProjectStore>(),
                provider.GetRequiredService<SessionRegistry>(),
                provider.GetRequiredService<ISessionManager>(),
                TimeProvider.System,
                provider.GetService<ITtySessionProviderResolver>(),
                provider.GetService<ISessionTranscriptStore>());
        });
        services.AddSingleton<IProjectEditor, StoreProjectEditor>();
        services.AddSingleton<IAssistantConversation, HostAssistantConversation>();
        services.AddSingleton<IExternalLinkOpener, NoBrowserLinkOpener>();
        services.AddSingleton<IUiHitchProbe, NoUiHitchProbe>();
        services.AddSingleton<IDesktopDisplays, NoDesktopDisplays>();
    }
}

// AC-1392: whether Build runs plugin phase 1. None for a backend that carries no plugins (a test, a probe); SafeMode
// discovers them but instantiates none (AC-478).
public enum PluginStartup
{
    None,
    Load,
    SafeMode,
}
