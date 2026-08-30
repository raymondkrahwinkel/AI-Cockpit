using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Configuration;
using Cockpit.Core.Updates;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Velopack;

namespace Cockpit.App;

sealed class Program
{
    // AC-883: internal rather than private so a view test can populate the container and then prove a pane did
    // *not* reach into it. With it null, "resolved nothing" and "was never allowed to resolve" look identical.
    public static IServiceProvider Services { get; internal set; } = null!;

    // How long a restart-launched instance waits for the outgoing one to release the single-instance claim before
    // giving up. The outgoing side is hard-exited by the exit watchdog within a few seconds (bug #32), and the
    // wait returns the moment the claim frees — this is only the ceiling for a shutdown that drags.
    private static readonly TimeSpan RestartHandoffWait = TimeSpan.FromSeconds(10);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run first so its headless install/update passes cannot touch ordinary startup state
        // (AC-385). Auto-apply only on the operator's request, never for the headless children below (AC-738).
        VelopackApp.Build().SetAutoApplyOnStartup(_AppliesAStagedUpdate(args)).Run();

        // Run Whisper calibration before the single-instance guard and app stack so its one-runtime-per-process
        // native load can measure, print, and exit in an isolated child (AC-68).
        if (Cockpit.Infrastructure.Voice.HeadlessCalibration.IsRequested(args))
        {
            Environment.Exit(Cockpit.Infrastructure.Voice.HeadlessCalibration.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        // Run dictation before the guard and app stack so Whisper's abort-prone native runtime is isolated in a
        // cheap child whose crash cannot take down the desktop (AC-174).
        if (Cockpit.Infrastructure.Voice.HeadlessDictation.IsRequested(args))
        {
            Environment.Exit(Cockpit.Infrastructure.Voice.HeadlessDictation.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        // Render before the guard and the app stack, like the two headless routes above: behind the guard it stood
        // down against the operator's running cockpit and exited 0 without a file (AC-1235). It needs neither — its
        // scenes carry their own view models — and ahead of them it writes nothing into the operator's state.
        if (Screenshotter.IsRequested(args))
        {
            Environment.Exit(Screenshotter.Run(args));

            return;
        }

        // Scrub inherited session identity (AC-42), terminal identity (#58), and credentials before any spawn.
        // Doing it once gives every route the same clean environment.
        ScrubInheritedHostEnvironment();

        // Acquire before housekeeping or plugin installation can delete files used by another cockpit (AC-4).
        // Development uses separate state; a marked restart waits through the intentional old/new overlap bounded
        // by the exit watchdog.
        var restartHandoff = args.Contains(AppRestartService.RestartArgument);
        using var singleInstance = SingleInstanceGuard.TryAcquire(
            CockpitBuild.IsDevelopment,
            restartHandoff ? RestartHandoffWait : TimeSpan.Zero);
        if (singleInstance is null)
        {
            _ShowAlreadyRunningNotice(args);

            return;
        }

        // Mark this process — and therefore every session it spawns — as running inside AI-Cockpit, so a nested
        // agent (a Claude CLI, a Codex app-server, a TTY) can detect it and adapt, the way tools key off
        // TERM_PROGRAM or TMUX. Set before anything can spawn a session.
        MarkCockpitEnvironment();

        // Before any state access, restrict legacy world-readable files and remove crash-left --mcp-config files
        // containing bearer headers. Run every startup rather than waiting for lazy service construction.
        CredentialFileHousekeeping.Run();

        var logPath = CockpitBuild.LogPath;
        var services = new ServiceCollection();

        // One logger factory shared between the pre-container plugin pass (below) and DI, so both write
        // to the same file — a second FileLoggerProvider would truncate the log a second time at startup.
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new Cockpit.App.Logging.FileLoggerProvider(logPath));
        });
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();

        // First line of every run, so the kept previous log says which process it was and how it was started —
        // a restart handoff and a hand launch leave the same trail otherwise.
        Cockpit.App.Logging.LifecycleLog.Use(loggerFactory);
        Cockpit.App.Logging.LifecycleLog.Write(
            $"Cockpit {Cockpit.Core.Plugins.HostVersionInfo.Current} starting: pid {Environment.ProcessId}, args [{string.Join(' ', args)}].");

        // A GUI or AppImage launch hands this process a PATH without the user's bin directories, and every child
        // inherits it (AC-19). Repair it once, up front, before anything resolves a tool or spawns a session.
        StartupPathRepair.Run(loggerFactory.CreateLogger(typeof(StartupPathRepair)));

        // AC-1093: a session that is not resumed after a crash still has its processes, and a build server or an
        // MSBuild node that systemd has adopted is no longer in any tree to find it by. Its cgroup outlived the run
        // that made it, and that is what this ends them by — before any session of this run makes a group of its own.
        StaleSessionProcessSweep.Run(loggerFactory.CreateLogger(typeof(StaleSessionProcessSweep)));

        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly);

        // Discover plugins before building the container so selected plugins can register services (#14), with
        // failures isolated. Safe mode is a UI-independent command-line escape hatch that still discovers plugins
        // for pending removals and management, but skips loading them (AC-478).
        var safeMode = args.Contains(PluginManager.SafeModeArgument);
        var pluginDiagnostics = new PluginDiagnostics();
        services.AddSingleton(pluginDiagnostics);
        var pluginManager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), pluginDiagnostics, safeMode);
        try
        {
            // Install bundled former-core plugins before discovery for first-run availability. This is best-effort
            // so failure cannot hide operator-installed plugins.
            _InstallBundledPlugins(loggerFactory);
#if DEBUG
            // Dev inner loop only: replace already-installed first-party plugins with their freshly built bytes,
            // so a rebuild lands in the sandbox without a hand copy. A release has no plugins-dev to find.
            _RefreshDevPlugins(loggerFactory);
#endif

            // The startup pass, which is the only thing that applies a staged update or a marked removal: this is
            // the one moment no plugin is loaded yet, which is the whole reason both are deferred to a restart.
            // Every other discovery in the app (the plugin manager's, the update checker's) reads and no more.
            var discoveredPlugins = new PluginBootstrap()
                .ApplyPendingChangesAndDiscoverAsync(AbstractionsContract.Version).GetAwaiter().GetResult();
            var pluginActivator = new PluginActivator(loggerFactory.CreateLogger<PluginActivator>());
            pluginManager.LoadAndConfigure(discoveredPlugins, services, pluginActivator.Activate);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<Program>().LogError(exception, "Plugin discovery failed; continuing without plugins.");
        }

        services.AddSingleton(pluginManager);

        // AC-1033: the knowledge base reads the loaded plugins' assemblies for the documentation they embed,
        // so it is registered after the manager it asks. Nothing is scanned until the help is first opened or
        // a `?` asks whether its target exists.
        services.AddSingleton<HelpService>();

        services.AddSessionPanes();

        Services = services.BuildServiceProvider();

        if (args.Contains("--audio-spike"))
        {
            AudioSpike.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // The MCP permission server (and any other IHostedService) must be running before the
        // first session spawns a CLI, and torn down cleanly on exit. The app uses a plain
        // ServiceProvider rather than a generic Host, so drive the hosted-service lifecycle here.
        var hostedServices = Services.GetServices<IHostedService>().ToArray();
        StartHostedServices(hostedServices);

        // Reconcile worktrees and compact state against one saved-pane roster so restorable panes are not treated
        // as orphans (AC-85/AC-409/AC-410). Register the shared task with the gate before it starts so restore waits.
        var reconcileGate = Services.GetRequiredService<IWorktreeReconcileGate>();
        var reconcileWorktreesAndCompactState = ReconcileWorktreesAndCompactStateAsync(
            Services.GetRequiredService<IWorktreeManager>(),
            Services.GetRequiredService<ISessionStateStore>(),
            Services.GetRequiredService<IWorkspaceSettingsStore>());
        reconcileGate.SignalStarted(reconcileWorktreesAndCompactState);
        _ = reconcileWorktreesAndCompactState;

        // Reconcile the repository-clone registry too (AC-90): forget any clone whose folder disappeared since last
        // run so the reuse check and the list reflect what is on disk. Fire-and-forget, and it only drops registry
        // entries — a clone folder that still exists is never deleted, because it may hold uncommitted work.
        _ = Services.GetRequiredService<IRepositoryCloneManager>().ReconcileAsync();

        // Log and handle recoverable dispatcher/render exceptions so one plugin surface cannot take down every
        // session and workspace. Fatal conditions still exit through their own paths.
        var uptime = System.Diagnostics.Stopwatch.StartNew();
        var lastRenderClockRecovery = Cockpit.App.Diagnostics.RenderClockRecovery.NeverRecovered;
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, exceptionEvent) =>
        {
            var logger = Services.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.App.UIThread");

            // AC-1236: read the tree before anything else, while it still carries what the cut pass left unfinished.
            if (Cockpit.App.Diagnostics.RenderClockRecovery.IsCutOff(exceptionEvent.Exception))
            {
                Cockpit.App.Diagnostics.LayoutLoopReport.Record(
                    _OpenWindows(), Cockpit.App.Diagnostics.LayoutLoopReport.RecordPathFor(logPath), logger);
            }

            if (logger is not null)
            {
                logger.LogError(exceptionEvent.Exception, "Unhandled UI-thread exception caught by the global net; the cockpit stays up.");
            }
            else
            {
                Console.Error.WriteLine($"Unhandled UI-thread exception caught by the global net; the cockpit stays up.\n{exceptionEvent.Exception}");
            }

            if (Cockpit.App.Diagnostics.RenderClockRecovery.ShouldRecover(
                    exceptionEvent.Exception, uptime.Elapsed - lastRenderClockRecovery))
            {
                lastRenderClockRecovery = uptime.Elapsed;
                logger?.LogWarning("renderclock restart requested after a cut-off layout pass (AC-1104).");
                _RequestRenderClockRestart();
            }

            exceptionEvent.Handled = true;
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // The last line the app itself can write. A log whose tail is ordinary activity and then simply stops
            // never reached here: nothing in the process asked to shut down, so it was ended from outside.
            Cockpit.App.Logging.LifecycleLog.Write("Desktop lifetime ended; tearing down sessions and exiting.");

            // A hard-exit watchdog bounds wedged teardown (#32), after bounded child cleanup. Deriving its deadline
            // from the teardown budget prevents the old 4s/10s mismatch from cutting off credential-file cleanup
            // while preserving prompt exit (AC-956).
            StartExitWatchdog(TeardownBudget + TimeSpan.FromSeconds(1));

            // After bounded child cleanup, hard-exit: Kestrel StopAsync can ignore cancellation while draining SSE,
            // and SoundFlow native disposal can hang. The OS reclaims the loopback socket anyway.
            DisposeCockpit();
            Environment.Exit(0);
        }
    }

    private static IReadOnlyList<Avalonia.Controls.Window> _OpenWindows() =>
        Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : [];

    // Posted rather than called here: this runs while the failed render operation is still unwinding, and only once
    // that has finished is MediaContext's own _nextRenderOp cleared — a request made before then would be dropped as
    // "a render is already scheduled". Any window will do; MediaContext is one instance per UI thread.
    private static void _RequestRenderClockRestart() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { Windows: [var window, ..] })
                {
                    window.RequestAnimationFrame(_ => { });
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);

    // Apply an operator-requested package only from a usable desktop launch, never a headless child or one that
    // will yield to an existing cockpit. Take the request last so an ineligible launch leaves it pending (AC-738).
    private static bool _AppliesAStagedUpdate(string[] args) =>
        !Cockpit.Infrastructure.Voice.HeadlessCalibration.IsRequested(args)
        && !Cockpit.Infrastructure.Voice.HeadlessDictation.IsRequested(args)
        && !SingleInstanceGuard.IsHeldByAnotherCockpit()
        && UpdateOnNextStart.TakeRequest();

    // The notice a refused second start shows (AC-4). Avalonia is started for this one window and nothing else:
    // Start() leaves the ApplicationLifetime null, so App.OnFrameworkInitializationCompleted builds no cockpit —
    // which is what makes it safe to do this with the app's own AppBuilder and get its theme and chrome for free.
    private static void _ShowAlreadyRunningNotice(string[] args)
    {
        BuildAvaloniaApp().Start((_, _) =>
        {
            using var dismissed = new CancellationTokenSource();
            var notice = new SingleInstanceNoticeDialog();
            notice.Closed += (_, _) => dismissed.Cancel();
            notice.Show();

            // The notice is the whole of this process's UI, so its own dispatcher loop is the app's: it runs until
            // the window is closed and then Main returns. There is no lifetime here to end it for us.
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(dismissed.Token);
        }, args);
    }

    // What the session teardown gets on the way out, and the figure the exit watchdog is set from — one constant,
    // so the two can never drift apart again (AC-956). Three seconds is what the watchdog used to allow in
    // practice; naming it here is what makes that a decision rather than a coincidence.
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(3);

    // Set by whichever route enters the teardown first, so the fallback below cannot run it a second time.
    private static int _teardownEntered;

    // AC-958: every await in the teardown chain posts its continuation to the dispatcher, so this only finishes while
    // the loop is still pumping — App holds the shutdown open and calls it there; from Main's finally it wedges.
    internal static async Task TearDownCockpitAsync()
    {
        if (Interlocked.Exchange(ref _teardownEntered, 1) == 1)
        {
            return;
        }

        // Started here and not only in Main's finally: while the shutdown is held open for this, nothing else bounds
        // the process, and a wedged teardown must never hold the exit up (bug #32).
        StartExitWatchdog(TeardownBudget + TimeSpan.FromSeconds(1));

        try
        {
            if (Services.GetService<CockpitViewModel>() is { } cockpit)
            {
                await AwaitTeardownAsync(cockpit.DisposeAsync().AsTask(), TeardownBudget, () => cockpit.PendingTeardownCount);
            }
        }
        finally
        {
            await DisposeServiceContainerAsync().ConfigureAwait(false);
        }
    }

    // AC-1202: its own bound, separate from TeardownBudget above — AwaitTeardownAsync can already spend the
    // full 3s against the 4s watchdog, so this gets what is safely left rather than a share of the whole budget.
    private static readonly TimeSpan ContainerDisposeBudget = TimeSpan.FromMilliseconds(800);

    // AC-1202: asynchronously disposes singleton resources; sync disposal rejects CockpitViewModel's async-only path.
    // Kept separate so tests can exercise it without starting TearDownCockpitAsync's exit watchdog.
    internal static async Task DisposeServiceContainerAsync()
    {
        if (Services is not IAsyncDisposable disposable)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().AsTask().WaitAsync(ContainerDisposeBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Cockpit.App.Logging.LifecycleLog.Write(
                $"Service container did not finish disposing within {ContainerDisposeBudget}; exiting without it.");
        }
    }

    // The bounded, logged half — its own method so a test can drive a teardown that wedges and one that throws.
    // AC-1134: `pendingCount`, when given, is read only once the budget is spent, so the exit log says how many
    // sessions had not finished tearing down instead of just falling silent.
    internal static async Task AwaitTeardownAsync(Task teardown, TimeSpan budget, Func<int>? pendingCount = null)
    {
        try
        {
            if (await Task.WhenAny(teardown, Task.Delay(budget)) == teardown)
            {
                await teardown;

                return;
            }

            var pendingClause = pendingCount is null ? string.Empty : $" {pendingCount()} session(s) had not finished tearing down.";
            Cockpit.App.Logging.LifecycleLog.Write($"Cockpit teardown did not finish within {budget}; exiting without it.{pendingClause}");
        }
        catch (Exception exception)
        {
            Cockpit.App.Logging.LifecycleLog.Write($"Cockpit teardown failed: {exception}");
        }
    }

    // Fallback for an exit that never reached the teardown while the dispatcher was alive: a no-op once it has run,
    // and bounded when it has not, because from here the chain wedges.
    private static void DisposeCockpit() => TearDownCockpitAsync().Wait(TeardownBudget);

    private static void StartHostedServices(IReadOnlyList<IHostedService> hostedServices)
    {
        foreach (var service in hostedServices)
        {
            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
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

    // A background hard-exit deadline ensures main-thread teardown cannot leave the process lingering at
    // "Application is shutting down..." (#32).
    private static void StartExitWatchdog(TimeSpan deadline)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(deadline);
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "cockpit-exit-watchdog",
        };
        watchdog.Start();
    }

    // Puts the plugins this build ships into the operator's plugins directory (see BundledPluginInstaller).
    // Best-effort: a plugin that cannot be copied is logged and skipped, and the app carries on with whatever
    // is already installed — a bundled plugin is a convenience, not a dependency.
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
                loggerFactory.CreateLogger<Program>().LogInformation(
                    "Installed the plugins shipped with this build: {Plugins}", string.Join(", ", installed));
            }
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<Program>().LogWarning(
                exception, "Could not install the bundled plugins; continuing with whatever is already installed.");
        }
    }

#if DEBUG
    // Refreshes already-installed first-party plugins from their freshly built output (see DevPluginInstaller):
    // the dev-machine half of the "installed copy does not move with source" fix. Best-effort and DEBUG only —
    // it only refreshes what is installed, never installs anything new, and finds nothing off a dev checkout.
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
                loggerFactory.CreateLogger<Program>().LogInformation(
                    "Refreshed first-party plugins from the dev build: {Plugins}", string.Join(", ", refreshed));
            }
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<Program>().LogWarning(
                exception, "Could not refresh dev plugins; continuing with whatever is already installed.");
        }
    }
#endif

    // Scrub host session markers (prevent transcript adoption, AC-42), terminal identity (prevent Ghostty styling,
    // #58), and inherited Anthropic credentials (prevent silent API billing) for every spawn route. Preserve
    // per-profile CLAUDE_CONFIG_DIR and generic COLORTERM; normalize TERM for terminal-independent rendering.
    private static void ScrubInheritedHostEnvironment()
    {
        var markers = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && Cockpit.Core.Sessions.Tty.TtyEnvironment.IsHostControlled(key))
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
        if (!string.IsNullOrEmpty(term)
            && !string.Equals(term, Cockpit.Core.Sessions.Tty.TtyEnvironment.TermValue, StringComparison.OrdinalIgnoreCase))
        {
            ProcessEnvironment.Assign("TERM", Cockpit.Core.Sessions.Tty.TtyEnvironment.TermValue);
        }
    }

    // A bare native-process AI_COCKPIT presence signal reaches every nested agent regardless of spawn path; callers
    // depend only on existence, not version or session detail (#45 D4 follow-up).
    private static void MarkCockpitEnvironment() => ProcessEnvironment.Assign("AI_COCKPIT", "1");

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(CockpitFontOptions())
            .LogToTrace();

        // Before DI, honor COCKPIT_RENDER_BACKEND or the saved macOS override while leaving default Metal detection
        // alone. The inert-on-other-platforms option enables same-build Metal memory comparisons (AC-57/AC-67).
        if (RenderBackendOverride.Resolve(RenderBackendConfig.Read()) is { } selection)
        {
            builder = builder.With(new AvaloniaNativePlatformOptions { RenderingMode = [.. selection.Modes] });
        }

        return builder;
    }

    // Add platform emoji fallbacks because Inter/Cascadia Mono lack those glyphs. Share the setup so headless
    // Screenshotter verifies the same font selection as the UI.
    internal static FontManagerOptions CockpitFontOptions() => new()
    {
        FontFallbacks =
        [
            new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
            new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") },
            new FontFallback { FontFamily = new FontFamily("Apple Color Emoji") },
        ],
    };
}
