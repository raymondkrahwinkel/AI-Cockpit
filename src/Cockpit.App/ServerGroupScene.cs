using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

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

    // AC-1457 (mockup v2 tab 3): the Health tab of an admin key, with the expired login, the alarm and a device code.
    public static MainWindow Health(int width, int height) => _Render(width, height, admin: true, health: true);

    // AC-1457 (mockup v2 tab 6): the same tab for an operate key; Run now stays, Sign in again gives way to a pointer.
    public static MainWindow HealthOperate(int width, int height) => _Render(width, height, admin: false, health: true);

    // One stand-in server, for a scene that shows what follows a group rather than the group itself.
    public static IRemoteServers StandIn(string name, RemoteServerState state) => new SceneServers(new SceneServer(name, state, []));

    private static MainWindow _Render(int width, int height, bool admin, bool asking = false, bool health = false)
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
        }, new SceneSignIns());

        var group = cockpit.ServerGroups[0];
        if (health)
        {
            group.Health.IsOpen = true;
            if (admin)
            {
                group.Health.StartSignInCommand.Execute(null);
            }

            return new MainWindow { DataContext = cockpit, Width = width, Height = height };
        }

        cockpit.OpenServerSessionCommand.Execute(group.Sessions[0]);
        if (group.Sessions[0].Pane is { } pane && admin && !asking)
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

        public IRemoteServerHealth Health { get; } = new SceneHealth(state.Key?.Capability == "admin");

        public ISessionRegistry? Sessions => this;

        public ISessionLauncher? Launcher => null;

        public IConnectKeyAdministration Administration { get; } = ServerAdminScene.StandIn();

        public IPluginAdministration Plugins { get; } = new ScenePlugins();

        public IServerProjects Projects { get; } = ServerAdminScene.ProjectsStandIn();

        public IServerProfiles Profiles { get; } = ServerAdminScene.ProfilesStandIn();

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

    // The stand-in's health: the mockup's own rows, as the server's route would send them.
    private sealed class SceneHealth(bool admin) : IRemoteServerHealth
    {
        public RemoteServerHealth? Current { get; } = _Mockup(admin);

        public bool IsWatched { get; set; }

        public event EventHandler? Changed
        {
            add
            {
            }

            remove
            {
            }
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RunActionAsync(string section, string actionId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        // A key with a narrowed scope learns only about itself, as the server answers it.
        private static RemoteServerHealth _Mockup(bool admin)
        {
            var now = DateTimeOffset.Now;
            List<RemoteHealthRow> runs = [new("15 scheduled · next: daily-checkin", false, now.AddHours(8), null)];
            foreach (var (name, outcome, failed, last, next, action, schedule) in new (string, string, bool, DateTimeOffset, DateTimeOffset, string?, string)[]
            {
                ("maintenance", "Done · 4 m", false, now.AddHours(-9), now.AddHours(15), "run:a", "daily 03:00"),
                ("morning-briefing", "Waiting for your permission", false, now.AddHours(-6), now.AddHours(18), null, "daily 06:00"),
                ("investor-data-fetch-ochtend", "Not run · Codex sign-in expired", true, now.AddHours(-5), now.AddHours(19), "run:c", "daily 06:45"),
                ("investor-rapport", "Sent to Discord", false, now.AddHours(-5), now.AddHours(19), "run:d", "daily 07:00"),
                ("skill-registration-audit", "Missed, caught up", false, now.AddDays(-5), now.AddDays(2), "run:e", "weekly Mon 09:00"),
                ("daily-checkin", "Done", false, now.AddDays(-1), now.AddHours(8), "run:f", "daily 18:30"),
            })
            {
                runs.Add(new($"{name} · {outcome}", failed, last, action, schedule, "Europe/Amsterdam"));
                runs.Add(new($"{name} · Next run", false, next, null));
            }

            return new RemoteServerHealth(
                [
                    new("server (Claude)", "Claude", "signedIn", now.AddMinutes(-5), null, null),
                    new("server (Codex)", "Codex CLI", "expired", now.AddMinutes(-5), now.AddHours(-3), now.AddHours(-3).AddMinutes(1)),
                    new("local-qwen", "Ollama (Hetzner)", "unchecked", now.AddMinutes(-5), null, null),
                ],
                new RemoteServerFacts(
                    "0.51.0",
                    "cockpit-server:0.51.0",
                    now.AddDays(-6).AddHours(-4),
                    "huis-cockpit.tailnet-ts.net:20383",
                    "laptop-raymond",
                    admin ? [new("laptop-raymond", "admin"), new("telefoon-raymond", "operate")] : [new("telefoon-raymond", "operate")]),
                [
                    new RemoteHealthSection("workflows-runs", true, runs),
                    new RemoteHealthSection("discord", true, [new("Online as Zyra · DM delivery ok", false, now.AddHours(-1), null)]),
                ]);
        }
    }

    private sealed class SceneSignIns : IServerSignIns
    {
        public ILoginFlow? Start(string server, string profile, CancellationToken cancellationToken) => new SceneSignIn();
    }

    // A device-code sign-in as Codex asks for one: a link and a code, and nothing to type.
    private sealed class SceneSignIn : ILoginFlow
    {
        public IAsyncEnumerable<LoginFlowStep> Steps => _Steps();

        public Task<LoginFlowResult> Completion { get; } = new TaskCompletionSource<LoginFlowResult>().Task;

        public Task SubmitAsync(string value, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<LoginFlowStep> _Steps()
        {
            yield return new LoginFlowStep(
                $"Open the link and enter the code there.{Environment.NewLine}Code: QX7K-2M9P",
                new Uri("https://auth.openai.com/device"),
                false)
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(14).AddSeconds(52),
            };
            await Task.CompletedTask;
        }
    }

    private sealed class ScenePlugins : IPluginAdministration
    {
        public Task<IReadOnlyList<Cockpit.Core.Plugins.InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Cockpit.Core.Plugins.InstalledPlugin>>([]);

        public Task<Cockpit.Core.Plugins.PluginStoreFetchResult> FetchStoreIndexAsync(Cockpit.Core.Plugins.PluginStoreConfig store, CancellationToken cancellationToken = default) => Task.FromResult(new Cockpit.Core.Plugins.PluginStoreFetchResult(false, null, null, null));

        public Task<Cockpit.Core.Plugins.PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Install from the server's store, or copy the zip onto the server.");

        public Task<Cockpit.Core.Plugins.PluginProvisionResult> InstallFromStoreAsync(Cockpit.Core.Plugins.PluginProvisionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new Cockpit.Core.Plugins.PluginProvisionResult(Cockpit.Core.Plugins.PluginProvisionOutcome.Failed, request.Id, request.Name, null, null, null, null));

        public Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        public Task<SessionRowSnapshot?> ReadRowsAtAsync(Func<long> lastSeq)
        {
            List<TranscriptSnapshotEntry> rows =
            [
                new("1", "UserText", "Prepare the morning briefing.", null, null, null, null, false, DateTimeOffset.UtcNow),
                new("2", "AssistantText", "Weather, agenda and the ROVA pick-up are in. I want to post the briefing to your Discord DM.", null, null, null, null, false, DateTimeOffset.UtcNow),
            ];
            if (asking)
            {
                rows.Add(new("3", "ToolUse", "Bash: curl -X POST $AIHUB_API_URL/notes", "Bash", "{\"command\":\"curl -X POST $AIHUB_API_URL/notes\"}", "scene-ask", null, false, DateTimeOffset.UtcNow)
                {
                    IsPendingPermission = true,
                });
            }

            return Task.FromResult<SessionRowSnapshot?>(new SessionRowSnapshot(rows, 0));
        }

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
