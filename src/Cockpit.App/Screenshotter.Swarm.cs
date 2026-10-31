using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Swarm;
using Cockpit.App.Services;
using Avalonia.Controls;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static AssistantChatWindow _AssistantSwarm(bool selfLeads = false, bool serverLeads = false, bool takeover = false, bool merging = false)
    {
        var cockpit = new CockpitViewModel();
        var options = new SidebarSwarmOptions(false);
        var leader = selfLeads ? options.Laptop : serverLeads ? options.Desktop with { Name = "Server", HostKind = SwarmHostKind.Server } : options.Desktop;
        var leadership = new SidebarLeadership(leader);
        var conversation = new SessionViewModel { Title = "Assistant", ActiveProfileLabel = "Zyra" };
        conversation.Transcript.Clear();
        conversation.QueuedMessages.Clear();
        conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "[from Laptop] Check the build on Desktop."));
        conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "dotnet build -warnaserror")
        {
            ToolName = "Bash", InputJson = "{\"command\":\"dotnet build -warnaserror\"}",
            ToolUseId = "build", ResultText = "Build succeeded. 0 warnings. 0 errors.",
        });
        conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            $"The build passed on {leader.Name}. You can continue from this machine."));
        if (takeover)
            conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider,
                Cockpit.Infrastructure.Swarm.SwarmTakeoverCoordinator.TakeoverNotice(DateTimeOffset.Now.Date.AddHours(14).AddMinutes(31))));
        if (merging)
        {
            conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider, ConversationMerge.Separator("Laptop")));
            conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "[from Laptop] Draft the release notes.")
                { WrittenWhileApartOn = "Laptop" });
            conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Release notes drafted on Desktop.")
                { WrittenWhileApartOn = "Desktop" });
        }
        var handle = new SessionPanelHandle(conversation, false, () => null);
        var nodes = new _SceneNodeSessions
        {
            OpenHandle = (node, pane) => node == leader.Name && pane == Cockpit.Core.Assistant.AssistantIdentity.PaneId
                ? handle : throw new InvalidOperationException("The chat must open its leader's assistant session."),
        };
        cockpit.ShowSwarm(options, leadership, nodes, poll: false);
        cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
        var chat = _AssistantChatViewModel(conversation, cockpit: cockpit);
        var reportedLeader = chat.LeadershipLabel;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(AssistantChatViewModel.LeadershipLabel)) reportedLeader = chat.LeadershipLabel;
        };
        chat.PropertyChanged += changed;
        leadership.MoveTo(selfLeads ? options.Desktop : options.Laptop);
        if (reportedLeader != (selfLeads ? "Led by Desktop" : "You lead"))
            throw new InvalidOperationException("The assistant chip must follow the leader change immediately.");
        leadership.MoveTo(leader);
        if (merging)
        {
            leadership.Merge(ConversationMerge.MergingNotice);
            if (reportedLeader != ConversationMerge.MergingNotice)
            {
                throw new InvalidOperationException("The assistant chip must say a merge is running.");
            }
        }
        chat.PropertyChanged -= changed;
        chat.EnsureOpenedAsync().GetAwaiter().GetResult();
        chat.InputText = "And check the tests next";
        if (!selfLeads && chat.Session is not { IsRemote: true, Transcript.Count: > 0 })
            throw new InvalidOperationException("A follower must draw the leader's real remote transcript.");
        return new AssistantChatWindow
        {
            DataContext = chat, Width = 960, Height = 720, Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
    }

    private static MainWindow _SwarmSidebar(int width, int height, bool asleep = false, bool start = false, bool empty = false)
    {
        var cockpit = new CockpitViewModel { SidebarWidth = 262, IsZoomed = true, AssistantIndicator = new AssistantIndicatorViewModel() };
        cockpit.Projects.StageDesignSample();
        var options = new SidebarSwarmOptions(empty);
        var leadership = new SidebarLeadership(options.Desktop);
        var started = false;
        var nodes = new _SceneNodeSessions
        {
            ReadSnapshot = name => new(name,
                started ? [new("desktop-review", "AC-1805 notification center", "Coder", "Awaiting your review", "NeedsAttention", true),
                    new("started", "Desktop project session", "Coder", "Ready", "Idle")]
                    : [new("desktop-review", "AC-1805 notification center", "Coder", "Awaiting your review", "NeedsAttention", true)],
                [new NodeScopedProfileSummary("Coder", default(SessionProvider), "Development", false)],
                [new NodeProjectRow("desktop-only", "Desktop checkout")]),
            StartSession = (name, profile, project) =>
            {
                if (name != "Desktop" || profile != "Coder" || project != "desktop-only")
                    throw new InvalidOperationException("Swarm sidebar must start with Desktop's own project and profile.");
                started = true;
                return new(null, "started", "Desktop project session", "Coder");
            },
        };
        cockpit.ShowSwarm(options, leadership, nodes, poll: false);
        cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
        if (empty)
        {
            if (cockpit.ServerGroups.Count != 0) throw new InvalidOperationException("No members must leave no empty remote group.");
        }
        else
        {
            var group = cockpit.ServerGroups.Single();
            if (!group.Leads || cockpit.LocalLeads) throw new InvalidOperationException("Desktop must lead initially.");
            options.Asleep = true;
            leadership.MoveTo(options.Laptop);
            if (group.Leads || !cockpit.LocalLeads)
                throw new InvalidOperationException("The leads pill must follow ISwarmLeadership.Changed before a refresh.");
            cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
            if (group.Leads || !cockpit.LocalLeads || group.IsConnected || !group.LastSeenLabel.StartsWith("Last seen", StringComparison.Ordinal))
                throw new InvalidOperationException("Leader change must update leads and keep Last seen rows.");
            if (!asleep)
            {
                options.Asleep = false;
                leadership.MoveTo(options.Desktop);
                cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
                cockpit.OpenServerStartCommand.ExecuteAsync(group).GetAwaiter().GetResult();
                group.Start.SelectedProject = group.Start.Projects.Single(project => project.Id == "desktop-only");
                if (!start)
                {
                    cockpit.StartOnServerCommand.ExecuteAsync(group).GetAwaiter().GetResult();
                    if (!started || cockpit.SelectedSession?.RemoteServer != "Desktop")
                        throw new InvalidOperationException("Starting on Desktop must open its Running on pane.");
                    cockpit.OpenServerSessionCommand.ExecuteAsync(group.Sessions[0]).GetAwaiter().GetResult();
                }
            }
        }
        return new MainWindow { DataContext = cockpit, Width = width, Height = height };
    }

    private sealed class SidebarLeadership(SwarmMember initial) : ISwarmLeadership
    {
        public SwarmMember? Current { get; private set; } = initial;
        public bool IsSelf => Current?.Name == "Laptop";
        public SwarmTakeover? PendingTakeover => null;
        public string? MergeNotice { get; private set; }
        public event EventHandler? Changed;
        public void MoveTo(SwarmMember member) { Current = member; Changed?.Invoke(this, EventArgs.Empty); }
        public void Merge(string notice) { MergeNotice = notice; Changed?.Invoke(this, EventArgs.Empty); }
        public Task TakeOverNowAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StayWithCurrentAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SidebarSwarmOptions(bool empty) : ISwarmOptions
    {
        public SwarmMember Desktop { get; } = new([1], "Desktop", 1, [], 20382, SwarmMemberId.FromPublicKey([1]), DateTimeOffset.UnixEpoch, 1);
        public SwarmMember Laptop { get; } = new([2], "Laptop", 2, [], 20382, SwarmMemberId.FromPublicKey([1]), DateTimeOffset.UnixEpoch, 2);
        public bool Asleep { get; set; }
        public SwarmJoinPending? Pending => null;
        public Task<IReadOnlyList<SwarmMachine>> MachinesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SwarmMachine>>(empty ? [] :
            [new(Laptop, true, "", new(SwarmPresence.Online, DateTimeOffset.UtcNow, true)),
             new(Desktop, false, "", new(Asleep ? SwarmPresence.Asleep : SwarmPresence.Online,
                 new DateTimeOffset(2026, 10, 30, 14, 32, 0, TimeSpan.FromHours(1)), true))]);
        public Task<SwarmSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(new SwarmSettings());
        public Task SaveSettingsAsync(SwarmSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeDiscoveryFound>>([]);
        public Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(SwarmJoinHandshake handshake, bool discovered, CancellationToken ct = default) => throw new NotSupportedException();
        public void Confirm(string pairingId) { }
        public void Refuse(string pairingId) { }
    }
}
