using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The consent card in front of <c>update_project</c> (AC-1059). It has to show, per named field, the value as
/// stored and the value it is about to become — "the assistant wants to change project X" alone is not something
/// an operator can approve or deny anything from.
/// </summary>
public sealed class AssistantUpdateProjectConsentTests : IDisposable
{
    private readonly RecordingBroker _consent = new();

    private readonly RecordingGateway _gateway = new();

    private AssistantAgentMcpTools _Tools(IConsentBroker? consent) =>
        new(_gateway, new RecordingAssistantMemory(), consent);

    [Fact]
    public async Task TheCard_ShowsTheStoredValueAndTheNewOne_ForEveryNamedField()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _gateway.Snapshot = new AssistantProjectSnapshot(
            "proj-1",
            "Invoices",
            "Old description",
            "/old/folder",
            "OldProfile",
            "Old prompt.",
            IsolateInWorktreeByDefault: false,
            EnabledMcpServerNames: ["depot"],
            "OldCategory",
            new Dictionary<string, string> { ["youtrack.project"] = "AC" });

        await _Tools(_consent).UpdateProjectAsync(
            "proj-1",
            name: "New name",
            description: "New description",
            sourceDirectory: "/new/folder",
            behaviorPrompt: "New prompt.",
            isolateInWorktreeByDefault: true,
            enabledMcpServerNames: ["youtrack"],
            category: "NewCategory");

        var asked = Assert.Single(_consent.Asked);
        Assert.Contains("name: Invoices -> New name", asked.Action);
        Assert.Contains("description: Old description -> New description", asked.Action);
        Assert.Contains("category: OldCategory -> NewCategory", asked.Action);

        // The four session-behaviour fields (AC-1059 criterion 4), grouped and called out rather than
        // interleaved with the ordinary ones above.
        Assert.Contains("This changes how every session on this project runs from here on:", asked.Action);
        Assert.Contains("folder: /old/folder -> /new/folder", asked.Action);
        Assert.Contains("MCP servers: depot -> youtrack", asked.Action);
        Assert.Contains("isolate in worktree by default: False -> True", asked.Action);
        Assert.Contains("before: Old prompt.", asked.Action);
        Assert.Contains("after: New prompt.", asked.Action);
    }

    private sealed class RecordingBroker : IConsentBroker
    {
        public List<ConsentRequest> Asked { get; } = [];

        public bool Approve { get; set; } = true;

        public event EventHandler<ConsentPrompt>? PromptOpened;

        public event EventHandler<Guid>? PromptClosed;

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            PromptOpened?.Invoke(this, null!);
            PromptClosed?.Invoke(this, Guid.Empty);
            return Task.FromResult(new ConsentDecision(
                Approve ? ConsentOutcome.Approved : ConsentOutcome.Denied, Remembered: false, Bypassed: false));
        }

        public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
        {
        }
    }

    private sealed class RecordingGateway : IAssistantAgentGateway
    {
        public AssistantProjectSnapshot? Snapshot { get; set; }

        public Task<AssistantProjectSnapshot?> GetProjectSnapshotAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<AssistantProjectUpdateResult> UpdateProjectAsync(
            string projectId,
            string? name = null,
            string? description = null,
            string? sourceDirectory = null,
            string? defaultProfileLabel = null,
            string? behaviorPrompt = null,
            bool? isolateInWorktreeByDefault = null,
            IReadOnlyList<string>? enabledMcpServerNames = null,
            string? category = null,
            IReadOnlyDictionary<string, string>? pluginFields = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AssistantProjectUpdateResult.Updated(projectId, name ?? Snapshot!.Name));

        public Task<AssistantProjectCreateResult> CreateProjectAsync(
            string name,
            string? description = null,
            string? sourceDirectory = null,
            string? defaultProfileLabel = null,
            string? behaviorPrompt = null,
            bool isolateInWorktreeByDefault = false,
            IReadOnlyList<string>? enabledMcpServerNames = null,
            string? category = null,
            IReadOnlyDictionary<string, string>? pluginFields = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
            string question, IReadOnlyList<(string Label, string? Description)> options, bool multiSelect, bool allowOther,
            string? header, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantProjectBindResult> BindSharedProjectAsync(
            string sharedProjectId,
            string sourceDirectory,
            string profileLabel,
            IReadOnlyList<string>? resourceReferences = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentStopResult> StopAsync(string paneId, SpawnCaller caller = SpawnCaller.Assistant, string? callerPaneId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantWatchResult> WatchSessionAsync(
            string paneId,
            IReadOnlyList<string>? events,
            int? afterMinutes = null,
            string? pattern = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose() => McpRequestContext.Set(null);
}
