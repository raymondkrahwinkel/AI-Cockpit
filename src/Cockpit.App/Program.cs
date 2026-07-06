using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Infrastructure;

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
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly);

        // Factory delegate so CockpitViewModel can mint a new ClaudeSessionViewModel (and,
        // transitively, its own IClaudeSession/CLI process) per "New session" click without
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
            // Order matters: dispose the cockpit (killing the child claude processes) before stopping
            // the MCP host, so those children release their permission-server connections first —
            // otherwise the server's graceful stop waits on the still-open SSE streams (bug #32).
            DisposeCockpit();
            StopHostedServices(hostedServices);
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

    private static void StopHostedServices(IReadOnlyList<IHostedService> hostedServices)
    {
        // Bound the stop: the MCP server's Kestrel host does a graceful drain, so give it a hard
        // deadline rather than CancellationToken.None — any straggling connection must not stall exit.
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var service in hostedServices)
        {
            try
            {
                service.StopAsync(shutdownTimeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Best-effort shutdown: a failing (or timed-out) stop must not mask the app exit.
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
