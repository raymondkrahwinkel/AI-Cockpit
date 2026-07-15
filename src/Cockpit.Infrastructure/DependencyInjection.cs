using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Infrastructure.Diagnostics;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice.GlobalHotkey;

namespace Cockpit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();

        AddDiagnostics(services);
        AddNotifications(services);
        AddPtyHost(services);
        AddGlobalHotkey(services);

        return services;
    }

    // Global push-to-talk (#34) is registered by platform here rather than via the Scrutor marker scan, for the
    // same reason the pty host is: the scan would bind whichever implementation it saw last to the single
    // IGlobalHotkeyService registration.
    //
    // Windows gets a SharpHook low-level keyboard hook. Linux depends on the session and not only the OS: under
    // Wayland nothing may install a keyboard hook, so the XDG GlobalShortcuts portal is the only route — but
    // under X11 the same hook Windows uses works, and routing every Linux to the portal threw that away. It
    // costs an X11 desktop the hotkey outright wherever its portal has no GlobalShortcuts implementation, which
    // is most of them. Anything else (macOS) has neither, and says so rather than pretending.
    private static void AddGlobalHotkey(IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IGlobalHotkeyService, SharpHookGlobalHotkeyService>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (_IsWaylandSession())
            {
                services.AddSingleton<IGlobalHotkeyService, PortalGlobalHotkeyService>();
            }
            else
            {
                services.AddSingleton<IGlobalHotkeyService, SharpHookGlobalHotkeyService>();
            }
        }
        else
        {
            services.AddSingleton<IGlobalHotkeyService, NoOpGlobalHotkeyService>();
        }
    }

    // Reading the two variables is all this does; what they mean is LinuxSession.IsWayland's, so that half is
    // testable off a Wayland session. Nothing here can be, which is the split.
    private static bool _IsWaylandSession() =>
        LinuxSession.IsWayland(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    // TTY mode's pty host (#9) is OS-specific for the same reason presence/toast are: it is
    // registered by platform here rather than via the Scrutor marker scan, which would otherwise
    // bind whichever of ConPtyHostFactory/PortaPtyHostFactory the assembly scan happened to see last
    // to the single IPtyHostFactory registration. TtyLauncher itself stays cross-platform and
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

    // The process table is read a different way on every OS (#78): /proc on Linux, ps on macOS, WMI on
    // Windows. Registered by platform for the same reason as the notifiers below — the Scrutor marker scan
    // would otherwise bind all three everywhere.
    private static void AddDiagnostics(IServiceCollection services)
    {
        // The analyzer cannot see that these branches are the platform check; the pragma says what the runtime
        // check already guarantees.
#pragma warning disable CA1416
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IProcessTableReader, WmiProcessTableReader>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IProcessTableReader, PsProcessTableReader>();
        }
        else
        {
            services.AddSingleton<IProcessTableReader, ProcProcessTableReader>();
        }
#pragma warning restore CA1416
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // #76: Linux used to get the no-op, so the "you are at the machine" half of the router delivered nothing
            // on the machine this cockpit is mostly used from.
            services.AddSingleton<IPresenceDetector, NoOpPresenceDetector>();
            services.AddSingleton<IToastNotifier, LinuxToastNotifier>();
        }
        else
        {
            // macOS keeps the no-op: there is no Mac here to try one on, and a notifier nobody has ever seen fire is
            // a claim, not a feature.
            services.AddSingleton<IPresenceDetector, NoOpPresenceDetector>();
            services.AddSingleton<IToastNotifier, NoOpToastNotifier>();
        }
    }
}
