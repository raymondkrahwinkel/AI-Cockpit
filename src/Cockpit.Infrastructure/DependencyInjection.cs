using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Claude.Permissions;
using Cockpit.Infrastructure.Claude.Tty;
using Cockpit.Infrastructure.Notifications;

namespace Cockpit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();
        services.AddTransient<IClaudeCliProcess, ClaudeCliProcess>();

        // One shared MCP permission server for the whole app: the same instance backs the
        // IPermissionServerState sessions read at spawn time and the IHostedService lifecycle.
        services.AddSingleton<PermissionMcpServer>();
        services.AddSingleton<IPermissionServerState>(sp => sp.GetRequiredService<PermissionMcpServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PermissionMcpServer>());

        AddNotifications(services);
        AddPtyHost(services);

        return services;
    }

    // TTY mode's pty host (#9) is OS-specific for the same reason presence/toast are: it is
    // registered by platform here rather than via the Scrutor marker scan, which would otherwise
    // bind whichever of ConPtyHostFactory/PortaPtyHostFactory the assembly scan happened to see last
    // to the single IPtyHostFactory registration. ClaudeTtyLauncher itself stays cross-platform and
    // just depends on whichever factory lands here.
    private static void AddPtyHost(IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IPtyHostFactory, ConPtyHostFactory>();
        }
        else
        {
            services.AddSingleton<IPtyHostFactory, PortaPtyHostFactory>();
        }
    }

    // Presence detection and the toast channel are OS-specific, so they are registered by platform
    // here rather than via the Scrutor marker scan (which would bind the Windows implementations on
    // Linux too). The cross-platform pieces — the Discord webhook notifier, the settings store, and
    // the AttentionNotifier orchestrator — carry ISingletonService and register through the scan.
    private static void AddNotifications(IServiceCollection services)
    {
        // A single shared HttpClient for the webhook POST — the recommended lifetime for a long-lived
        // client that talks to one host, avoiding socket exhaustion from per-call HttpClient instances.
        services.AddSingleton<HttpClient>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IPresenceDetector, WindowsPresenceDetector>();
            services.AddSingleton<IToastNotifier, WindowsToastNotifier>();
        }
        else
        {
            services.AddSingleton<IPresenceDetector, NoOpPresenceDetector>();
            services.AddSingleton<IToastNotifier, NoOpToastNotifier>();
        }
    }
}
