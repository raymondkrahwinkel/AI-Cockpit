using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Hosting;

// AC-1381: the backend's startup, for the desktop and for a process with no App. It comes in steps because the desktop
// runs its single-instance guard and its plugin pass in between; each step keeps the moment it had in `Program.cs`.
public sealed class CockpitBackend
{
    private Task? _reconcile;

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
    // so a frontend's own seams replace the defaults.
    public static CockpitBackend Build(ILoggerFactory loggerFactory, Action<IServiceCollection>? frontend = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
        _AddNoFrontendDefaults(services);
        frontend?.Invoke(services);

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

    // AC-1380's planners. Never before `Start`: the worktree planner's crash net assumes the startup sweep has run, and
    // the rest read a registry the reconcile's roster decides. The Depot watcher polls only what its `BoundProjects`
    // names, which a frontend that owns a project list sets before this.
    public void StartPlanners()
    {
        if (_reconcile is null)
        {
            throw new InvalidOperationException("The planners start after the startup reconcile, and Start has not run it yet.");
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
