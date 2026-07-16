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
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App;

sealed class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

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
        // Cockpit is a GUI app, but when it is launched from a terminal (e.g. Ghostty) it inherits that
        // terminal's self-identification — TERM_PROGRAM=ghostty, GHOSTTY_*, TERM=xterm-ghostty. The SvcSystems
        // (XTerm.NET) renderer picks that up through the Avalonia/Skia text stack and draws every line
        // underlined; a desktop launch has none of these set, so it does not. Strip the host-terminal identity
        // from this process's own environment before Avalonia starts — the same markers TtyEnvironment already
        // scrubs for the claude pty (#58), so the pty inherits a clean environment too.
        ScrubHostTerminalIdentity();

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

        var logPath = Path.Combine(CockpitBuild.StateRoot, "logs", "cockpit.log");
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

        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly);

        // #14 Plugins — phase 1, before the container is built: discover the plugins installed next to
        // cockpit.json and let each load-decided plugin register its own services. The manager isolates a
        // plugin that fails to load or configure; a discovery failure leaves the app running without plugins.
        var pluginDiagnostics = new PluginDiagnostics();
        services.AddSingleton(pluginDiagnostics);
        var pluginManager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), pluginDiagnostics);
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

            var discoveredPlugins = new PluginBootstrap()
                .DiscoverAsync(AbstractionsContract.Version).GetAwaiter().GetResult();
            var pluginActivator = new PluginActivator(loggerFactory.CreateLogger<PluginActivator>());
            pluginManager.LoadAndConfigure(discoveredPlugins, services, pluginActivator.Activate);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<Program>().LogError(exception, "Plugin discovery failed; continuing without plugins.");
        }

        services.AddSingleton(pluginManager);

        // Factory delegate so CockpitViewModel can mint a new SessionViewModel (and,
        // transitively, its own ISessionDriver/CLI process) per "New session" click without
        // holding an injected IServiceProvider itself (service-locator anti-pattern — Code.md §2).
        services.AddTransient<Func<SessionViewModel>>(
            provider => () => provider.GetRequiredService<SessionViewModel>());

        // Same factory pattern for the TTY-mode panel (#9 experiment).
        services.AddTransient<Func<TtyViewModel>>(
            provider => () => provider.GetRequiredService<TtyViewModel>());

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
            // transcript, and "--scene <name>" to render a dialog instead of the main window.
            var sceneIndex = Array.IndexOf(args, "--scene");
            var scene = sceneIndex >= 0 && sceneIndex + 1 < args.Length ? args[sceneIndex + 1] : null;

            var sizeIndex = Array.IndexOf(args, "--size");
            if (sizeIndex >= 0 && sizeIndex + 1 < args.Length &&
                args[sizeIndex + 1].Split('x') is [var rawWidth, var rawHeight] &&
                int.TryParse(rawWidth, out var width) && int.TryParse(rawHeight, out var height))
            {
                Screenshotter.Run(args[screenshotIndex + 1], width, height, scene);
                return;
            }

            Screenshotter.Run(args[screenshotIndex + 1], scene: scene);
            return;
        }

        // The MCP permission server (and any other IHostedService) must be running before the
        // first session spawns a CLI, and torn down cleanly on exit. The app uses a plain
        // ServiceProvider rather than a generic Host, so drive the hosted-service lifecycle here.
        var hostedServices = Services.GetServices<IHostedService>().ToArray();
        StartHostedServices(hostedServices);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // A background watchdog guarantees the process dies promptly even if a teardown step
            // wedges — the exit must never hang again (bug #32). It fires a hard exit after a short
            // deadline; the child claude processes are killed in DisposeCockpit first, so nothing is
            // orphaned.
            StartExitWatchdog(TimeSpan.FromSeconds(4));

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

    private static void DisposeCockpit()
    {
        if (Services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return;
        }

        // A bounded wait so a wedged session teardown can't hang the exit; the child processes are
        // killed early in each session's DisposeAsync, so timing out here still leaves nothing behind.
        cockpit.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
    }

    private static void StartHostedServices(IReadOnlyList<IHostedService> hostedServices)
    {
        foreach (var service in hostedServices)
        {
            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
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

    // Avalonia configuration, don't remove; also used by visual designer.
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int unsetenv(string name);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

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

    // Removes the host terminal's self-identification from this process's own environment (see the call in
    // Main). Reuses the same marker set TtyEnvironment scrubs for the claude pty (#58): TERM_PROGRAM(_VERSION)
    // covers every terminal that sets it, plus the GHOSTTY_* vars, and TERM is normalised to a generic
    // terminfo name whatever terminal launched us — so the render is deterministic and terminal-independent.
    // COLORTERM is deliberately left untouched — it is a generic truecolor-support signal, not a terminal id.
    private static void ScrubHostTerminalIdentity()
    {
        var markers = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && Cockpit.Core.Sessions.Tty.TtyEnvironment.IsHostTerminalIdentityMarker(key))
            {
                markers.Add(key);
            }
        }

        foreach (var key in markers)
        {
            RemoveFromEnvironment(key);
        }

        // A terminal-specific TERM (e.g. xterm-ghostty) is what the SvcSystems/Skia render stack keys off,
        // drawing every line underlined; normalise anything that is not already the generic value.
        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term)
            && !string.Equals(term, Cockpit.Core.Sessions.Tty.TtyEnvironment.TermValue, StringComparison.OrdinalIgnoreCase))
        {
            AssignEnvironment("TERM", Cockpit.Core.Sessions.Tty.TtyEnvironment.TermValue);
        }
    }

    // Presence signal for nested agents (#45 D4 follow-up): a bare AI_COCKPIT=1, no version or per-session detail —
    // a consumer keys off the variable existing. Via AssignEnvironment so it lands in the native environment too,
    // which is what a spawned process inherits whichever path launches it; every session spawn inherits this
    // process's environment, so this one assignment reaches all of them (Claude CLI, Codex app-server, TTY).
    private static void MarkCockpitEnvironment() => AssignEnvironment("AI_COCKPIT", "1");

    // Environment.SetEnvironmentVariable updates .NET's managed copy but does not reliably remove the variable
    // from the native (libc) environment that Skia and other native libraries read via getenv — so on Unix we
    // also call unsetenv/setenv directly. Without this the scrub has no effect on the render and the host
    // terminal's identity still leaks through (the underline bug when Cockpit is launched from Ghostty).
    private static void RemoveFromEnvironment(string key)
    {
        Environment.SetEnvironmentVariable(key, null);
        if (!OperatingSystem.IsWindows())
        {
            unsetenv(key);
        }
    }

    private static void AssignEnvironment(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        if (!OperatingSystem.IsWindows())
        {
            setenv(key, value, 1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(CockpitFontOptions())
            .LogToTrace();

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
