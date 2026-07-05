using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zyra.Voice.Core;
using Zyra.Voice.Infrastructure;

namespace Zyra.Voice.App;

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
            typeof(Zyra.Voice.Core.DependencyInjection).Assembly,
            typeof(Zyra.Voice.Infrastructure.DependencyInjection).Assembly,
            typeof(Program).Assembly);

        Services = services.BuildServiceProvider();

        if (args.Contains("--audio-spike"))
        {
            AudioSpike.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
