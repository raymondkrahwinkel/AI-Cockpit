using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

// AC-1456: the main window with a connect server as a group beside this laptop (mockup v2 tabs 2 and 6). The server is
// a stand-in: the same view models over a registry and a state that no network fills.
internal static class ServerGroupScene
{
    // An admin key that holds the assistant: the header line, the assistant line and a remote pane asking to be stopped.
    public static MainWindow Admin(int width, int height) => _Render(width, height, admin: true);

    // AC-1469 (mockup v2 tab 2): the open pane stands on a permission prompt its key may answer.
    public static MainWindow Permission(int width, int height) => _Render(width, height, admin: true, asking: true);

    // An operate key with a scope: "Admin" locked, the assistant held elsewhere, and the start card with one profile.
    public static MainWindow Operate(int width, int height) => _Render(width, height, admin: false);

    // One stand-in server, for a scene that shows what follows a group rather than the group itself.
    public static IRemoteServers StandIn(string name, RemoteServerState state) => new SceneServers(new SceneServer(name, state, []));

    private static MainWindow _Render(int width, int height, bool admin, bool asking = false)
    {
        var cockpit = new CockpitViewModel();
        var started = DateTimeOffset.UtcNow.AddDays(-6).AddHours(-4);
        var key = admin
            ? new RemoteServerKey("laptop-raymond", "admin", true, "laptop-raymond", "0.66.0", started, MayAnswerPermissions: true)
            : new RemoteServerKey("telefoon-raymond", "operate", false, "laptop-raymond", "0.66.0", started, MayAnswerPermissions: true);
        SceneHandle[] sessions = admin
            ? [
                new("morning-briefing", "server (Claude)", SessionStatus.NeedsAttention, asking),
                new("AC-1421", "server (Claude)", SessionStatus.Busy),
                new("research-monitor", "server (Claude)", SessionStatus.Done),
            ]
            : [
                new("morning-briefing", "server (Claude)", SessionStatus.NeedsAttention),
                new("research-monitor", "server (Claude)", SessionStatus.Done),
            ];
        var server = new SceneServer("huis-cockpit", new RemoteServerState(true, 38, key), sessions);
        cockpit.ShowServers(new SceneServers(server), async (handle, name) =>
        {
            var pane = new SessionViewModel(SessionControls.DesignTime);
            await pane.FollowRemoteAsync(handle, name);
            return pane;
        });

        var group = cockpit.ServerGroups[0];
        cockpit.OpenServerSessionCommand.Execute(group.Sessions[0]);
        if (group.Sessions[0].Pane is { } pane && admin)
        {
            pane.IsConfirmingClose = true;
        }

        if (!admin)
        {
            group.Start.Fill(
                [new NodeProfileChoice("server (Claude)", null)],
                [new NodeProjectChoice("personal", "Personal"), new NodeProjectChoice("depot", "depot")]);
            group.Start.Prompt = "Pick up AC-1421";
            group.Start.IsOpen = true;
        }

        return new MainWindow { DataContext = cockpit, Width = width, Height = height };
    }

    private sealed class SceneServers(IRemoteServer server) : IRemoteServers
    {
        public IReadOnlyList<IRemoteServer> Servers { get; } = [server];

        public event EventHandler? Changed
        {
            add
            {
            }

            remove
            {
            }
        }

        public bool Knows(string name) => Servers.Any(server => server.Name == name);

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(string name) => Task.CompletedTask;

        public Task ReconnectAsync(string name) => Task.CompletedTask;
    }

    private sealed class SceneServer(string name, RemoteServerState state, IReadOnlyList<ISessionHandle> sessions)
        : IRemoteServer, ISessionRegistry
    {
        public string Name { get; } = name;

        public ISessionRegistry? Sessions => this;

        public ISessionLauncher? Launcher => null;

        public RemoteServerState State { get; } = state;

        public IReadOnlyList<ISessionHandle> All { get; } = sessions;

        public ISessionHandle? Assistant => null;

        public event EventHandler? StateChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public event EventHandler? Changed
        {
            add
            {
            }

            remove
            {
            }
        }

        public ISessionHandle? Find(string paneId) => All.FirstOrDefault(handle => handle.PaneId == paneId);
    }

    // A session on the stand-in server: two rows for its pane, and nothing it can be asked to do.
    private sealed class SceneHandle(string title, string profile, SessionStatus status, bool asking = false) : ISessionHandle
    {
        public string PaneId { get; } = title;

        public string Title { get; } = title;

        public string WorkspaceId => "node";

        public string? PlacedWorkspaceId => "node";

        public string? WorkingDirectory => null;

        public string? WorktreeBranch => null;

        public string? ActiveProfileLabel { get; } = profile;

        public bool IsTerminal => false;

        public bool IsEmbedded => false;

        public SessionStatus SessionStatus { get; } = status;

        public string Statusline => "";

        public bool CanTakeAPrompt => false;

        public bool DeliversInboxAtTurnStart => false;

        public bool HasPromptWaitingToBeDelivered => false;

        public bool HasPendingConsent => false;

        public int ProcessCount => 0;

        public double ProcessCpuPercent => 0;

        public long ProcessMemoryBytes => 0;

        public int AbandonedProcessCount => 0;

        public bool HasReadableTranscript => true;

        public Task<SessionRowSnapshot?> ReadRowsAtAsync(Func<long> lastSeq) => Task.FromResult<SessionRowSnapshot?>(new SessionRowSnapshot(
            [
                new TranscriptSnapshotEntry("1", "UserText", "Prepare the morning briefing.", null, null, null, null, false, DateTimeOffset.UtcNow),
                new TranscriptSnapshotEntry("2", "AssistantText", "Weather, agenda and the ROVA pick-up are in. I want to post the briefing to your Discord DM.", null, null, null, null, false, DateTimeOffset.UtcNow),
                .. asking
                    ?
                    [
                        new TranscriptSnapshotEntry(
                            "3", "ToolUse", "Bash: curl -X POST $AIHUB_API_URL/notes", "Bash", "{\"command\":\"curl -X POST $AIHUB_API_URL/notes\"}", "scene-ask", null, false, DateTimeOffset.UtcNow)
                        {
                            IsPendingPermission = true,
                        },
                    ]
                    : [],
            ],
            0));

        public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(false);

        public Task<SessionTranscriptSlice> ReadTranscriptAsync(int count) => Task.FromResult(new SessionTranscriptSlice([], 0));

        public Task<bool> SendPromptAsync(string prompt) => Task.FromResult(false);

        public Task<bool?> SubmitPromptWhenReadyAsync(string prompt) => Task.FromResult<bool?>(null);

        public Task SetWorktreeBranchAsync(string? branch) => Task.CompletedTask;

        public Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow) => Task.FromResult(false);

        public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

        public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync() => Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]);

        public Task<bool> SetStatuslineAsync(string statusline) => Task.FromResult(false);

        public Task<bool> SuggestNameAsync(string name) => Task.FromResult(false);

        public Task<SessionWakeState> ReadWakeStateAsync() => Task.FromResult(new SessionWakeState(false, SessionStatus, false));
    }
}
