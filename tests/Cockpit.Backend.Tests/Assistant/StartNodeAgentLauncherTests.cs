using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Assistant;

/// <summary>
/// AC-1375 acceptance 2: <c>start_node_agent</c> reaches <see cref="ISessionLauncher"/> through the moved gateway with
/// the profile, desk and prompt it reached <c>CockpitViewModel</c> with before, and a launcher that starts nothing
/// hands the paired controller the same sentence the app's own start gave it.
/// </summary>
public sealed class StartNodeAgentLauncherTests : IDisposable
{
    private const string ProfileLabel = "Laptop Sonnet";

    private readonly Workspace _desk = Workspace.Create("Sessions", WorkspaceType.Sessions);

    public StartNodeAgentLauncherTests() => McpRequestContext.Set(NodeCallerIdentity.PaneId);

    public void Dispose() => McpRequestContext.Set(null);

    [Fact]
    public async Task StartNodeAgent_ReachesTheLauncher_WithTheProfileDeskAndPromptItWasGiven()
    {
        var launcher = new RecordingLauncher(_desk, new LaunchedSession("pane-1", "tests (from the controller)", PromptDelivered: false));

        var answer = await _Tools(launcher).StartNodeAgentAsync(ProfileLabel, prompt: "Run the tests.", name: "tests (from the controller)");

        var request = Assert.Single(launcher.Starts);
        Assert.Equal(
            (ProfileLabel, _desk.Id, "Run the tests.", "tests (from the controller)", (string?)null, false),
            (request.Profile.Label, request.WorkspaceId, request.Prompt, request.SessionName, request.WorkingDirectory, request.StartedByTheAssistant));
        Assert.Contains("\"ok\":true", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALauncherThatStartsNothing_GivesTheControllerTheSameSentenceAsBefore()
    {
        var answer = await _Tools(new RecordingLauncher(_desk, started: null)).StartNodeAgentAsync(ProfileLabel);

        Assert.Contains("The cockpit could not start a session just now.", answer, StringComparison.Ordinal);
    }

    private static NodeSessionMcpTools _Tools(ISessionLauncher launcher)
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SessionProfile>>([new SessionProfile(ProfileLabel, new ClaudeConfig("/fake/.claude"))]));
        var pairing = Substitute.For<INodePairingBroker>();
        pairing.IsProfileAllowed(ProfileLabel).Returns(true);

        var gateway = new AssistantAgentGateway(
            new SessionRegistry(),
            launcher,
            Substitute.For<IProjectEditor>(),
            Substitute.For<ISessionWatcher>(),
            Substitute.For<IAssistantConversation>(),
            Substitute.For<IExternalLinkOpener>(),
            profiles,
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            new AgentMessageInbox(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>());

        return new NodeSessionMcpTools(
            Substitute.For<IAssistantReadGateway>(),
            gateway,
            pairing,
            profiles,
            new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")),
            new AgentMessageInbox(),
            Substitute.For<IAssistantMemory>());
    }

    // Only what a start touches: the desk list the node picks its desk from, and the start itself.
    private sealed class RecordingLauncher(Workspace desk, LaunchedSession? started) : ISessionLauncher
    {
        public List<SessionLaunchRequest> Starts { get; } = [];

        public WorkspaceSettings Workspaces { get; } = new() { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

        public Task<T> RunExclusiveAsync<T>(Func<T> decision) => Task.FromResult(decision());

        public bool CanCloseWorkspace(string workspaceId) => throw new NotSupportedException();

        public bool ProfileHasTtyRoute(SessionProfile profile) => throw new NotSupportedException();

        public Task<Project?> FindProjectByIdAsync(string projectId) => throw new NotSupportedException();

        public Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request)
        {
            Starts.Add(request);
            return Task.FromResult(started);
        }

        public Task StopSessionAsync(string paneId) => throw new NotSupportedException();

        public Task<bool> SetSessionNameAsync(string paneId, string name) => throw new NotSupportedException();

        public Task<Workspace> CreateSessionsWorkspaceAsync(string name) => throw new NotSupportedException();

        public Task RenameWorkspaceAsync(string workspaceId, string name) => throw new NotSupportedException();

        public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) => throw new NotSupportedException();
    }
}
