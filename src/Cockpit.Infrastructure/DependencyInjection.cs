using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Infrastructure.Diagnostics;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Hotkeys;

namespace Cockpit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();

        // AC-1110: handed to DelegationService as a deferred lookup, not as the instance. The catalog reaches that
        // service back through ICockpitInternalMcpProvider, and injecting it directly closes a construction cycle
        // that deadlocks the container instead of being reported.
        services.AddSingleton<Func<IMcpServerCatalog?>>(provider => provider.GetService<IMcpServerCatalog>);

        // Built-in cockpit MCP endpoints (#AC-13): CockpitMcpEndpointHost hosts each and auto-publishes it to the
        // registry as its own MCP server. cockpit-session is always mounted: telling the operator what a session
        // is working on is plumbing, not a capability to weigh up, so it's kept out of the pickers (#AC-12).
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-session", typeof(SessionStatusTools), AlwaysMounted: true));

        // cockpit-worktrees (AC-104): the tools an agent uses to isolate a subtask in its own git worktree and clean
        // it up when done. Its own server, like cockpit-session, so a delegated sub-agent has it too.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-worktrees", typeof(Worktrees.WorktreeTools)));

        // cockpit-verify (AC-86): the visual verify loop — runs the project's registered render command behind an
        // operator consent and feeds the rendered UI back to the session as a snapshot, so UI work is not
        // delivered blind (Iron Law #9). The agent can only trigger a registered runner, never choose the command.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-verify", typeof(Verify.VerifyMcpTools)));

        // cockpit-terminal (AC-34, phase 1): lets an agent read a terminal pane the operator has open, live and gated
        // by an Approve/Deny consent. Behind the master switch, off by default. Registered via a factory so the
        // IsEnabled gate can read the live TerminalAccessState singleton.
        services.AddSingleton(provider => new CockpitMcpEndpoint(
            "cockpit-terminal",
            typeof(Terminal.TerminalMcpTools),
            () => provider.GetRequiredService<Terminal.TerminalAccessState>().Enabled));

        // cockpit-shell (AC-1066): the shell a session with none of its own otherwise lacks (Bash is a Claude Code
        // CLI feature, not something the cockpit supplies). AlwaysMounted, like cockpit-session/cockpit-agents, so a
        // delegated task gets it too and it stays preloaded above the search_tools threshold. Off by default.
        services.AddSingleton(provider => new CockpitMcpEndpoint(
            "cockpit-shell",
            typeof(Shell.ShellMcpTools),
            () => provider.GetRequiredService<Shell.ShellAccessState>().Enabled,
            AlwaysMounted: true));

        // cockpit-agents (AC-391, AC-392): agent-to-agent communication — list_agents, notify/read_inbox.
        // AlwaysMounted because it is a delivery route, not a capability to opt into: when it was tickable, a
        // profile with any saved MCP selection could silently miss it, so a message could be sent but unreadable.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-agents", typeof(Agents.AgentsMcpTools), AlwaysMounted: true));

        // cockpit-assistant (AC-544): the voice assistant's read path across every workspace — the reach no
        // ordinary session has, so it's Internal rather than AlwaysMounted: it reaches only a launch that names
        // it by name (AssistantSessionHost). Second gate is in the tools, which check the caller's pane.
        services.AddSingleton(new CockpitMcpEndpoint(
            Cockpit.Core.Assistant.AssistantIdentity.McpServerName,
            typeof(Assistant.AssistantReadMcpTools),
            Internal: true));

        // cockpit-assistant-agents (AC-545): the acting half — start_agent and stop_agent. Internal for the same
        // two-gate reason as its read neighbour, and a separate endpoint because the read server promises nothing
        // on it changes anything. Internal is load-bearing: without it, these tools fan out to any session.
        services.AddSingleton(new CockpitMcpEndpoint(
            Cockpit.Core.Assistant.AssistantIdentity.ActMcpServerName,
            typeof(Assistant.AssistantAgentMcpTools),
            Internal: true));

        // cockpit-node (AC-795): sessions on this machine, as its paired controller cockpit may see and drive
        // them. Must NOT be Internal — an Internal endpoint binds no network listener (AC-791), which is the one
        // thing this needs. `IsEnabled: false` keeps it off; the real gate is the per-tool pane check below.
        services.AddSingleton(new CockpitMcpEndpoint(
            "cockpit-node",
            typeof(Mcp.NodeSessionMcpTools),
            IsEnabled: () => false));

        // The advisory cross-instance claim behind AC-71 — one implementation, so (unlike the hotkey service
        // below) there is nothing for a platform switch to choose between.
        services.AddSingleton<IHotkeyExclusivityGuard, MutexHotkeyExclusivityGuard>();

        AddDiagnostics(services);
        AddNotifications(services);
        AddPtyHost(services);
        AddSessionMemoryLimiter(services);
        AddGlobalHotkey(services);
        AddScreenshotCapture(services);
        AddScreenLockMonitor(services);

        return services;
    }

    // OS screen-lock detection (AC-5) is registered by platform here rather than via the Scrutor marker scan,
    // which would bind whichever implementation it saw last. Windows: SessionSwitch; macOS: CoreFoundation;
    // Linux: systemd-logind over D-Bus. Anything else gets the null monitor, so the feature is simply inert.
    private static void AddScreenLockMonitor(IServiceCollection services)
    {
#pragma warning disable CA1416
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<Core.Secrets.IScreenLockMonitor, Security.WindowsScreenLockMonitor>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<Core.Secrets.IScreenLockMonitor, Security.MacScreenLockMonitor>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Core.Secrets.IScreenLockMonitor, Security.LinuxScreenLockMonitor>();
        }
        else
        {
            services.AddSingleton<Core.Secrets.IScreenLockMonitor, Core.Secrets.NullScreenLockMonitor>();
        }
#pragma warning restore CA1416
    }

    // Global push-to-talk (#34) is registered by platform here rather than via the Scrutor marker scan, which
    // would bind whichever implementation it saw last. Windows uses a SharpHook keyboard hook. Linux splits on
    // session type: Wayland needs the XDG GlobalShortcuts portal, X11 uses the same hook as Windows.
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

    // Screen capture (AC-220) is registered by platform for the same reason the hotkey above is. Unlike the
    // hotkey, Linux does not split on session type: the Screenshot portal serves both X11 and Wayland. Windows
    // reads the virtual screen through GDI, macOS through screencapture, anything else says it cannot.
    private static void AddScreenshotCapture(IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The screen reader is registered alongside rather than resolved by the marker scan: it is
            // Windows-only, and a scan that bound it everywhere would drag GDI into the graph on Linux.
            services.AddSingleton<IWindowsScreenReader, Win32ScreenReader>();
            services.AddSingleton<IScreenshotCapture, WindowsScreenshotCapture>();
            services.AddSingleton<IDesktopWindows, Win32DesktopWindows>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IMacScreenReader, MacScreenReader>();
            services.AddSingleton<IScreenshotCapture, MacScreenshotCapture>();
            services.AddSingleton<IDesktopWindows, MacDesktopWindows>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IScreenshotCapture, PortalScreenshotCapture>();

            // Window picking splits on session type where capture does not (AC-330): X11 publishes window
            // geometry/stacking, Wayland does not, and this app is an XWayland client there, seeing only a
            // fraction of the operator's windows via the X11 property.
            if (_IsWaylandSession())
            {
                services.AddSingleton<IDesktopWindows, UnsupportedDesktopWindows>();
            }
            else
            {
                services.AddSingleton<IDesktopWindows, X11DesktopWindows>();
            }
        }
        else
        {
            services.AddSingleton<IScreenshotCapture, UnsupportedScreenshotCapture>();
            services.AddSingleton<IDesktopWindows, UnsupportedDesktopWindows>();
        }
    }

    // Reading the two variables is all this does; what they mean is LinuxSession.IsWayland's, so that half is
    // testable off a Wayland session. Nothing here can be, which is the split.
    private static bool _IsWaylandSession() =>
        LinuxSession.IsWayland(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    // TTY mode's pty host (#9) is OS-specific, registered by platform here rather than via the Scrutor marker
    // scan, which would bind whichever of ConPtyHostFactory/PortaPtyHostFactory it saw last. TtyLauncher stays
    // cross-platform and just depends on whichever factory lands here.
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

    // The session memory cap (AC-661) is watched, never enforced by a kernel-level kill, on any platform (AC-692):
    // Linux throttles via cgroup `memory.high`, Windows and macOS share the polling watchdog since neither has a
    // native soft-throttle primitive.
    private static void AddSessionMemoryLimiter(IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<ISessionMemoryLimiter, LinuxCgroupMemoryLimiter>();
        }
        else
        {
            services.AddSingleton<ISessionMemoryLimiter, PollingMemoryLimiter>();
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
            services.AddSingleton<ICrashLogReader, WindowsCrashLogReader>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IProcessTableReader, PsProcessTableReader>();
            services.AddSingleton<ICrashLogReader, MacCrashLogReader>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IProcessTableReader, ProcProcessTableReader>();
            services.AddSingleton<ICrashLogReader, LinuxCrashLogReader>();
        }
        else
        {
            services.AddSingleton<IProcessTableReader, ProcProcessTableReader>();
            services.AddSingleton<ICrashLogReader, NoOpCrashLogReader>();
        }
#pragma warning restore CA1416
    }

    // Presence detection and the toast channel are OS-specific, registered by platform here rather than via
    // the Scrutor marker scan (which would bind the Windows implementations on Linux too). Cross-platform
    // pieces (Discord notifier, settings store, AttentionNotifier) carry ISingletonService and use the scan.
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
