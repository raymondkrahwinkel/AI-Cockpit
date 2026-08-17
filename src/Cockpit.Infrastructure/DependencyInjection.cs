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

        // Built-in cockpit MCP endpoints (#AC-13): CockpitMcpEndpointHost hosts each and auto-publishes it to the
        // registry as its own MCP server. cockpit-session carries set_status and is always mounted: telling the
        // operator what a session is working on is cockpit plumbing, not a capability to weigh up, so it is kept out
        // of the pickers rather than offered as something to untick. Available to every session (including delegated
        // sub-agents, unlike the orchestrator). A plugin adds its own the same way (#AC-12).
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-session", typeof(SessionStatusTools), AlwaysMounted: true));

        // cockpit-worktrees (AC-104): the tools an agent uses to isolate a subtask in its own git worktree and clean
        // it up when done. Its own server, like cockpit-session, so a delegated sub-agent has it too.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-worktrees", typeof(Worktrees.WorktreeTools)));

        // cockpit-verify (AC-86): the visual verify loop — runs the project's registered render command behind an
        // operator consent and feeds the rendered UI back into the session as a text snapshot (plus a screenshot
        // where the provider can see it), so UI work is not delivered blind (Iron Law #9). Its own server, like
        // cockpit-worktrees; the agent can only trigger a registered runner, never choose the command.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-verify", typeof(Verify.VerifyMcpTools)));

        // cockpit-terminal (AC-34, phase 1): lets an agent read a terminal pane the operator has open, live and gated
        // by an Approve/Deny consent. Behind the master switch — while it is off (the default) the endpoint is hosted
        // but not advertised to any session, so for an agent the feature does not exist. Registered via a factory so
        // the IsEnabled gate can read the live TerminalAccessState singleton.
        services.AddSingleton(provider => new CockpitMcpEndpoint(
            "cockpit-terminal",
            typeof(Terminal.TerminalMcpTools),
            () => provider.GetRequiredService<Terminal.TerminalAccessState>().Enabled));

        // cockpit-diagram (AC-810): lets an agent read and edit a diagram surface the operator has open, gated by a
        // per-capability Approve/Deny (AC-830 removed the standing master switch this used to sit behind).
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-diagram", typeof(Diagrams.DiagramMcpTools)));

        // cockpit-whiteboard (AC-823): lets an agent read a screenshot of a whiteboard surface the operator has
        // open and, since AC-854 lifted AC-820's "never writes to the canvas" boundary, put objects on it one at a
        // time — read and write each behind their own Approve/Deny, as cockpit-diagram above does.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-whiteboard", typeof(Whiteboard.WhiteboardMcpTools)));

        // cockpit-wireframe (AC-872): the third collab surface — an agent writes a screen sketch in the wireframe
        // text format and edits it component by component, read and edit each behind their own Approve/Deny. Its
        // own server rather than tools on cockpit-diagram: add_node and add_component are two vocabularies (AC-864).
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-wireframe", typeof(Wireframe.WireframeMcpTools)));

        // cockpit-agents (AC-391, AC-392): the agent-to-agent communication line — list_agents to see who else is on
        // your desk, notify/read_inbox to send them a message and collect your own. AlwaysMounted, like
        // cockpit-session and unlike cockpit-verify/cockpit-worktrees, because this one is now a delivery route
        // rather than a capability each session weighs up on its own: while it was tickable, a profile that had ever
        // saved an explicit MCP selection (McpServerRegistryFilter.ApplySessionSelection) — including an empty one —
        // silently did not get it, and a message line that is absent for some of the sessions on a desk is not a
        // line. The sender is told its message was delivered and the recipient has no tool to read it with, which is
        // worse than not having the feature. The cost is the tool definitions in every session's context; the
        // alternative is a route whose reliability depends on a checkbox nobody remembers ticking.
        services.AddSingleton(new CockpitMcpEndpoint("cockpit-agents", typeof(Agents.AgentsMcpTools), AlwaysMounted: true));

        // cockpit-assistant (AC-544): the voice assistant's read path across every workspace — the reach no ordinary
        // session has, and the reason this one is Internal rather than AlwaysMounted like its neighbour above. An
        // internal endpoint stays out of every picker and out of the no-selection fan-out, so it reaches only a launch
        // that names it by name, and AssistantSessionHost is the single place that does. That is the first of the two
        // gates; the second is in the tools themselves, which refuse any caller whose transport-verified pane is not
        // the assistant's. Deliberately both: the mount is configuration, and configuration widens by accident.
        services.AddSingleton(new CockpitMcpEndpoint(
            Cockpit.Core.Assistant.AssistantIdentity.McpServerName,
            typeof(Assistant.AssistantReadMcpTools),
            Internal: true));

        // cockpit-assistant-agents (AC-545): the acting half — start_agent and stop_agent. Internal for exactly the
        // same two-gate reason as its read neighbour above, and a second endpoint rather than two more tools on that
        // one because the read server's documented promise is that nothing on it changes anything (see
        // AssistantIdentity.ActMcpServerName). Internal is load-bearing, not tidiness: delete it and these tools fan
        // out to every session that named no selection, where the only thing left between an ordinary agent and a
        // spawn on any desk is the per-tool pane check.
        services.AddSingleton(new CockpitMcpEndpoint(
            Cockpit.Core.Assistant.AssistantIdentity.ActMcpServerName,
            typeof(Assistant.AssistantAgentMcpTools),
            Internal: true));

        // cockpit-node (AC-795): the sessions on this machine, as the cockpit paired to it as its controller may
        // see and drive them. The inverse of the two endpoints above and for the mirror-image reason: those are
        // Internal, hosted but reachable only from a launch that names them, while this one must *not* be Internal
        // — an Internal endpoint binds no network listener at all (AC-791), which is the one thing this endpoint
        // exists to have. `IsEnabled` returning false is what keeps it off this machine instead: hosted, so the node
        // listener stands in front of it, and advertised to no local session, so nothing here can call it. The gate
        // that holds is neither of those but the per-tool check in `NodeSessionMcpTools`, which refuses any caller
        // whose transport-verified pane is not the node's reserved one.
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

    // OS screen-lock detection (AC-5) is registered by platform here rather than via the Scrutor marker scan, for the
    // same reason the pty host and hotkey are: the scan would bind whichever implementation it saw last to the single
    // IScreenLockMonitor registration. Windows reads SystemEvents.SessionSwitch; macOS the CoreFoundation distributed
    // notification; Linux systemd-logind over D-Bus. Anything else gets the null monitor, so the feature is simply
    // inert there rather than a missing registration — the runtime selection always yields a working object.
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

    // Screen capture (AC-220) is registered by platform for the same reason the hotkey above is: one
    // registration, three implementations, and the Scrutor marker scan would bind whichever it saw last.
    //
    // Unlike the hotkey, Linux does not split on the session type. The Screenshot portal is served by
    // xdg-desktop-portal on X11 as well as Wayland — it predates GlobalShortcuts and every desktop that ships a
    // screenshot tool backs it — so there is no X11 hole here to route around. Windows reads the virtual screen
    // through GDI, macOS through screencapture, and anything else says it cannot rather than pretending.
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

            // Window picking splits on the session type where capture does not (AC-330). X11 publishes window
            // geometry and stacking; Wayland deliberately does not, and this app is an XWayland client there —
            // which sees only other XWayland windows, so the property that works on X11 would list a fraction of
            // the operator's windows and quietly omit the rest.
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
