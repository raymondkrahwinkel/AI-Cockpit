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
        // Installing, updating and uninstalling all re-run this executable with arguments Velopack owns, and this
        // call is what handles them and ends the process (AC-385). It is deliberately the first statement in Main:
        // anything placed above it runs during every one of those passes, in a window nobody sees — a second
        // cockpit claiming the single-instance lock mid-update, plugins being installed by an installer, a log
        // being truncated.
        //
        // Auto-apply only when the operator asked for it (AC-738): applying is an action they take, and this same
        // executable is re-run as the headless children below. On an ordinary launch this still reads the
        // installation on disk — it is not a no-op — and then returns.
        VelopackApp.Build().SetAutoApplyOnStartup(_AppliesAStagedUpdate(args)).Run();

        // Headless calibration child (AC-68): a measurement of one Whisper backend, spawned by the running cockpit
        // because Whisper.net loads its native runtime once per process. This must be the very first thing Main
        // does — before the single-instance guard (which would refuse a second cockpit), before Avalonia, plugins
        // and DI — so the child pays for none of that and only measures, prints its result, and exits.
        if (Cockpit.Infrastructure.Voice.HeadlessCalibration.IsRequested(args))
        {
            Environment.Exit(Cockpit.Infrastructure.Voice.HeadlessCalibration.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        // Headless dictation worker (AC-174): the transcription child the running cockpit spawns so Whisper's native
        // runtime — which can abort() and take a process down — loads here, isolated, instead of in the desktop. Same
        // reason and same placement as the calibration child above: before the single-instance guard, Avalonia and DI,
        // none of which a transcription worker should pay for. A native crash in here kills only this child.
        if (Cockpit.Infrastructure.Voice.HeadlessDictation.IsRequested(args))
        {
            Environment.Exit(Cockpit.Infrastructure.Voice.HeadlessDictation.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        // Strip everything the host owns from this process's own environment before Avalonia starts or anything
        // spawns a child: the agent-session markers of a Claude Code session the cockpit may have been launched
        // from (else a spawned session adopts the parent's id — AC-42), the host terminal's self-identification
        // (which drew every line underlined under Ghostty — #58), and any inherited Anthropic credential. Doing it
        // once here means every spawn route inherits a clean base rather than each re-deriving its own scrub.
        ScrubInheritedHostEnvironment();

        // Only one cockpit at a time (AC-4). This goes first because the housekeeping directly below it deletes
        // --mcp-config files, and the bundled-plugin install further down deletes plugin directories: run those
        // in a second cockpit and they take them out from under the sessions of the first, which is still using
        // them. A development build is exempt and keeps its state elsewhere — see CockpitBuild.
        //
        // A restart is the one case where two cockpits overlapping is intended: AppRestartService launches the new
        // one before the old one has finished shutting down and released the claim. It marks that launch, and the
        // new instance waits out the brief handoff instead of losing the race and refusing to start — the exit
        // watchdog bounds the old side to a few seconds, so RestartHandoffWait comfortably covers it.
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

        // Before anything reads or writes the cockpit's state: restrict the files an older version left
        // world-readable, and delete the --mcp-config files (bearer headers and all) that a crash or that same
        // older version left behind. Both must happen on every start, not when some lazily-built service
        // happens to be constructed.
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

        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly);

        // #14 Plugins — phase 1, before the container is built: discover the plugins installed next to
        // cockpit.json and let each load-decided plugin register its own services. The manager isolates a
        // plugin that fails to load or configure; a discovery failure leaves the app running without plugins.
        //
        // AC-478 safe mode: a command-line switch, consistent with this file's other one-shot startup flags
        // (--screenshot, --audio-spike) rather than an env var or a settings-file toggle — the whole point is a
        // way in that does not depend on any UI (including the settings screen a broken plugin might have
        // wedged) rendering successfully first. Discovery still runs below (a pending removal must still apply,
        // and the operator still needs Plugin manager to see what is installed); only PluginManager's load
        // phase is skipped.
        var safeMode = args.Contains(PluginManager.SafeModeArgument);
        var pluginDiagnostics = new PluginDiagnostics();
        services.AddSingleton(pluginDiagnostics);
        var pluginManager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), pluginDiagnostics, safeMode);
        try
        {
            // The plugins this build ships (transcript search, git status) are put in place before discovery, so
            // they are simply there on first run — no install step for something that used to be a core feature.
            // Failing to install one must not cost the operator the plugins they installed themselves, so this
            // is best-effort and discovery runs regardless.
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

        services.AddSessionPanes();

        Services = services.BuildServiceProvider();

        if (args.Contains("--audio-spike"))
        {
            AudioSpike.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        if (screenshotIndex >= 0)
        {
            if (screenshotIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("--screenshot requires an output PNG path argument.");
                Environment.Exit(1);
                return;
            }

            // Optional "--size WxH" so a docs render can use a window big enough to show a session's
            // transcript, "--scene <name>" to render a dialog instead of the main window, and
            // "--snapshot <path>" to also dump the laid-out visual tree as text (AC-86 verify loop).
            var sceneIndex = Array.IndexOf(args, "--scene");
            var scene = sceneIndex >= 0 && sceneIndex + 1 < args.Length ? args[sceneIndex + 1] : null;

            var snapshotIndex = Array.IndexOf(args, "--snapshot");
            var snapshotPath = snapshotIndex >= 0 && snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : null;

            // "--snapshot-target <x:Name>" scopes the text snapshot to one control's subtree.
            var targetIndex = Array.IndexOf(args, "--snapshot-target");
            var snapshotTarget = targetIndex >= 0 && targetIndex + 1 < args.Length ? args[targetIndex + 1] : null;

            var sizeIndex = Array.IndexOf(args, "--size");
            if (sizeIndex >= 0 && sizeIndex + 1 < args.Length &&
                args[sizeIndex + 1].Split('x') is [var rawWidth, var rawHeight] &&
                int.TryParse(rawWidth, out var width) && int.TryParse(rawHeight, out var height))
            {
                Screenshotter.Run(args[screenshotIndex + 1], width, height, scene, snapshotPath, snapshotTarget);
                return;
            }

            Screenshotter.Run(args[screenshotIndex + 1], scene: scene, snapshotPath: snapshotPath, snapshotTarget: snapshotTarget);
            return;
        }

        // The MCP permission server (and any other IHostedService) must be running before the
        // first session spawns a CLI, and torn down cleanly on exit. The app uses a plain
        // ServiceProvider rather than a generic Host, so drive the hosted-service lifecycle here.
        var hostedServices = Services.GetServices<IHostedService>().ToArray();
        StartHostedServices(hostedServices);

        // Reconcile the worktree registry against a fresh start (AC-85/AC-410), and fold duplicate session-state
        // records left by earlier runs (AC-409) — both against the same roster of AI-session panes cockpit.json
        // still names, so a worktree or a state record belonging to a pane a restore may yet bring back is never
        // treated as orphaned just because nothing has rebuilt it into a live session yet. One fire-and-forget task
        // for both, handed to IWorktreeReconcileGate before its own body starts running (see the method) so a
        // restore that reaches the gate finds this task waiting rather than a stale "already complete" placeholder.
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

        // Global UI-thread safety net: a plugin body — or any dispatcher work — that throws while rendering must never
        // take the whole cockpit down with it (a render exception in one workspace was tearing the process down). Log it
        // and mark it handled so the app keeps running: the surface that threw fails on its own, every other session,
        // terminal and workspace survives. A genuinely fatal condition still ends the process through other paths; this
        // only stops a recoverable UI exception from being terminal.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, exceptionEvent) =>
        {
            var logger = Services.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.App.UIThread");
            if (logger is not null)
            {
                logger.LogError(exceptionEvent.Exception, "Unhandled UI-thread exception caught by the global net; the cockpit stays up.");
            }
            else
            {
                Console.Error.WriteLine($"Unhandled UI-thread exception caught by the global net; the cockpit stays up.\n{exceptionEvent.Exception}");
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

            // A background watchdog guarantees the process dies promptly even if a teardown step
            // wedges — the exit must never hang again (bug #32). It fires a hard exit after a short
            // deadline; the child claude processes are killed in DisposeCockpit first, so nothing is
            // orphaned.
            //
            // AC-956: the watchdog's deadline and the teardown's own budget are derived from one another rather
            // than written twice. They used to be 4 seconds and 10, which meant the watchdog always won and
            // teardown was cut off at whatever it was doing at 4 — in practice its tail, where a session deletes
            // the temp files it wrote. Five days of leftover mcp-configs (28 of them holding a bearer header)
            // were the visible half of that. The exit stays as prompt as it was; what changed is that the
            // teardown now gets a budget it can actually finish inside, and cannot silently outlive it.
            StartExitWatchdog(TeardownBudget + TimeSpan.FromSeconds(1));

            // Kill the child claude processes (DisposeCockpit is internally bounded), then hard-exit.
            // We deliberately do NOT gracefully stop the MCP host: its Kestrel StopAsync was seen to
            // block for minutes at "Application is shutting down..." draining a lingering SSE stream
            // (ignoring its cancellation token), and a graceful drain buys nothing before
            // Environment.Exit — the OS reclaims the loopback socket and its OS-assigned port on
            // process death. Environment.Exit also sidesteps the singleton SoundFlow AudioEngine's
            // native-thread dispose, which can itself hang on the miniaudio join.
            DisposeCockpit();
            Environment.Exit(0);
        }
    }

    // Whether this launch applies the package the operator asked for on their last visit (AC-738). Ruled out for the
    // headless children this executable is re-run as, and for a launch that will stand down against a cockpit that is
    // already running — applying force-stops that one. The request is taken last, so a launch that cannot use it
    // leaves it for the one that can.
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

    private static void DisposeCockpit()
    {
        if (Services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return;
        }

        // A bounded wait so a wedged session teardown can't hang the exit; the child processes are
        // killed early in each session's DisposeAsync, so timing out here still leaves nothing behind.
        cockpit.DisposeAsync().AsTask().Wait(TeardownBudget);
    }

    private static void StartHostedServices(IReadOnlyList<IHostedService> hostedServices)
    {
        foreach (var service in hostedServices)
        {
            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    // AC-410: the worktree reconcile and the session-state compaction both need "which AI-session panes will this
    // start offer back" — SessionRestoreRoster.PaneIdsAsync reads that once from cockpit.json, so the two cannot
    // each derive a different answer (and, unlike before panes were persisted, compaction can now safely drop a
    // pane's state instead of never dropping any).
    private static async Task ReconcileWorktreesAndCompactStateAsync(
        IWorktreeManager worktreeManager,
        ISessionStateStore sessionStateStore,
        IWorkspaceSettingsStore workspaceSettingsStore)
    {
        var restorablePaneIds = await SessionRestoreRoster.PaneIdsAsync(workspaceSettingsStore).ConfigureAwait(false);

        // Reconcile the worktree registry against a fresh start (AC-85): no session is alive yet, so a worktree
        // outside this roster is orphaned — a clean one is removed with its branch, one that holds work is kept
        // and marked retained, and git's stale admin entries are pruned. A worktree the roster does name survives
        // even though nothing has rebuilt its pane into a live session yet — a restore may still reattach it.
        await worktreeManager.ReconcileAsync(restorablePaneIds).ConfigureAwait(false);

        // Fold duplicate session-state records left by earlier runs (AC-409), now against the same roster: a pane
        // no longer named in cockpit.json has its state dropped instead of kept forever. Run after the reconcile
        // above so a worktree it just kept for a restorable pane is never the one compaction treats as gone.
        await sessionStateStore.CompactAsync(restorablePaneIds).ConfigureAwait(false);
    }

    // Belt-and-suspenders against a wedged shutdown: a background thread that hard-exits after a
    // deadline no matter what the main-thread teardown is doing. This is the "hard exit after a
    // graceful timeout" fallback the earlier #32 work anticipated — with it the process can never
    // linger at "Application is shutting down..." again.
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

    // Removes from this process's own environment everything the host owns and must not hand down to a spawned
    // child (see the call in Main) — the same set TtyEnvironment scrubs for the claude pty, applied once here so
    // every spawn route (TTY, SDK, MCP stdio) inherits a clean base instead of each re-deriving its own scrub with
    // different coverage (AC-42). That set is:
    //   - the markers of the agent session the cockpit was launched from (CLAUDECODE / CLAUDE_CODE_* /
    //     CLAUDE_AGENT_*): an inherited CLAUDE_CODE_SESSION_ID makes a child adopt the parent's session id and
    //     write its turns into the parent's transcript (AC-42). CLAUDE_CONFIG_DIR is deliberately not in this set
    //     and is re-applied per profile;
    //   - the host terminal's self-identification (TERM_PROGRAM(_VERSION), GHOSTTY_*): the pty child is rendered by
    //     Cockpit's own Exclr8 emulator, and a leaked TERM_PROGRAM=ghostty caused every line to draw underlined (#58);
    //   - any inherited Anthropic credential (ANTHROPIC_*): one that reaches the CLI silently moves the session onto
    //     API-key billing.
    // A normal desktop launch has none of these set, so this is a no-op there; it bites exactly when the cockpit is
    // started from a shell that exports one. TERM is normalised to a generic terminfo name so the render is
    // terminal-independent. COLORTERM is deliberately left untouched — a generic truecolor signal, not an identity.
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

    // Presence signal for nested agents (#45 D4 follow-up): a bare AI_COCKPIT=1, no version or per-session detail —
    // a consumer keys off the variable existing. Via ProcessEnvironment so it lands in the native environment too,
    // which is what a spawned process inherits whichever path launches it; every session spawn inherits this
    // process's environment, so this one assignment reaches all of them (Claude CLI, Codex app-server, TTY).
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

        // AC-57/AC-67: force a non-default macOS render backend when the operator picked one in Options (or set
        // COCKPIT_RENDER_BACKEND, which wins), otherwise leave UsePlatformDetect()'s Metal auto-selection alone.
        // The config is read directly here because this runs before the DI host; AvaloniaNativePlatformOptions is
        // read only by the macOS backend, so applying it is inert on Windows/Linux. This is what lets a tester run
        // the same build on OpenGL/Software to isolate whether Metal drives the runaway native-memory growth.
        if (RenderBackendOverride.Resolve(RenderBackendConfig.Read()) is { } selection)
        {
            builder = builder.With(new AvaloniaNativePlatformOptions { RenderingMode = [.. selection.Modes] });
        }

        return builder;
    }

    // Emoji fallback so Claude's ✅/🔧/📊/⚠️ render as glyphs instead of tofu boxes — the UI fonts
    // (Inter, Cascadia Mono) carry no emoji. Skia picks the first installed family per platform
    // (Segoe UI Emoji on Windows, Noto Color Emoji on Linux, Apple Color Emoji on macOS). Shared so
    // the headless Screenshotter renders the same fallbacks it verifies against.
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
