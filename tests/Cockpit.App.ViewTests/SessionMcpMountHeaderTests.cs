using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Sessions;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-927: the header names the servers the launch route reported it mounted, not the checklist the session was
/// started from — a selection never holds the always-mounted, auto-mounted or project-linked ones.
/// </summary>
[Collection("avalonia")]
public class SessionMcpMountHeaderTests
{
    [Fact]
    public void AReportedMount_ReplacesTheSelectionTheHeaderCounts()
    {
        var (statusLine, servers) = Dispatcher.UIThread.Invoke(() =>
        {
            var mounts = new SessionMcpMounts();
            var cockpit = _Cockpit(mounts);
            var session = new SessionViewModel
            {
                McpServerSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youtrack" },
            };
            cockpit.Sessions.Add(session);

            mounts.Report(session.PaneId, ["youtrack", "cockpit-session", "cockpit-agents"]);

            return (session.ConnectedStatusLine, Servers: session.McpServerSelection);
        });

        Assert.Equal("Connected (3 MCP servers).", statusLine);
        Assert.Contains("cockpit-agents", servers!);
    }

    /// <summary>
    /// AC-997: a server that fell over stays in the selection instead of reading as one the operator never
    /// checked, and the header's own line says something is wrong without needing the hover.
    /// </summary>
    [Fact]
    public void AServerThatFailedToConnect_StaysInTheSelection_AndTheHeaderSaysSomethingFellOver()
    {
        var (statusLine, servers, tooltip) = Dispatcher.UIThread.Invoke(() =>
        {
            var mounts = new SessionMcpMounts();
            var cockpit = _Cockpit(mounts);
            var session = new SessionViewModel
            {
                McpServerSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youtrack", "git" },
            };
            cockpit.Sessions.Add(session);

            mounts.Report(
                session.PaneId,
                ["youtrack"],
                [new McpServerConnectionIssue("git", "/home/raymond is not a valid Git repository")]);

            return (session.ConnectedStatusLine, Servers: session.McpServerSelection, session.McpServersTooltip);
        });

        Assert.Equal("Connected (2 MCP servers, 1 could not connect).", statusLine);
        Assert.Contains("git", servers!);
        Assert.Contains("git — could not connect: /home/raymond is not a valid Git repository", tooltip);
    }

    /// <summary>
    /// A report for a pane the host no longer holds is dropped rather than throwing: a session can be closed
    /// while its launch is still resolving its servers.
    /// </summary>
    [Fact]
    public void AReportForAPaneTheHostNoLongerHolds_IsDropped()
    {
        var exception = Record.Exception(() => Dispatcher.UIThread.Invoke(() =>
        {
            var mounts = new SessionMcpMounts();
            _ = _Cockpit(mounts);

            mounts.Report("gone", ["youtrack"]);
        }));

        Assert.Null(exception);
    }

    private static CockpitViewModel _Cockpit(SessionMcpMounts mounts)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            sessionMcpMounts: mounts);
    }
}
