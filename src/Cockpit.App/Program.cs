using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App;

sealed class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit", "logs", "cockpit.log");
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

        // Factory delegate so CockpitViewModel can mint a new ClaudeSessionViewModel (and,
        // transitively, its own ISessionDriver/CLI process) per "New session" click without
        // holding an injected IServiceProvider itself (service-locator anti-pattern — Code.md §2).
        services.AddTransient<Func<ClaudeSessionViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeSessionViewModel>());

        // Same factory pattern for the TTY-mode panel (#9 experiment).
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

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

            Screenshotter.Run(args[screenshotIndex + 1]);
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
