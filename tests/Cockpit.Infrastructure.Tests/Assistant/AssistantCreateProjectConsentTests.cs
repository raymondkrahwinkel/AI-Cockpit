using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The consent card in front of <c>create_project</c> (AC-799, criteria 4 and 5). The card has to show
/// <c>sourceDirectory</c>, the MCP choice, the worktree default and the behaviour prompt as their own labelled
/// lines — not a name alone — because those four are what decide how every session on this project runs.
/// </summary>
public sealed class AssistantCreateProjectConsentTests : IDisposable
{
    private readonly RecordingBroker _consent = new();

    private readonly RecordingGateway _gateway = new();

    private AssistantAgentMcpTools _Tools(IConsentBroker? consent) =>
        new(_gateway, new RecordingAssistantMemory(), consent);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // ── Criterion 4: the four fields on the card, each its own line ────────────────────────────────────────────



    [Fact]
    public async Task AnEmptyEnabledMcpServerNamesArray_ReadsAsEveryServer_OnTheCardAndInWhatIsStored()
    {
        // AC-799 review finding 1: `[]` and leaving the argument out have to mean the same thing wherever a
        // consumer looks at it — the card the operator approves, and what actually gets passed on to be stored.
        // Before this fix the card said "every server" for `[]` while `ProjectMcpOverlay.IsSelectedByDefault`
        // reads a non-null empty list as "select nothing" — so the operator would have approved "every server"
        // and the project would have come out with none ticked.
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools(_consent).CreateProjectAsync("Invoices", enabledMcpServerNames: []);

        var asked = Assert.Single(_consent.Asked);
        Assert.Contains("MCP servers: (every server, following the registry)", asked.Action);
        Assert.Null(_gateway.LastEnabledMcpServerNames);
    }

    // ── Criterion 5: the risk class ─────────────────────────────────────────────────────────────────────────────


    // ── Newline/control-character forgery on the single-line fields ────────────────────────────────────────────

    [Fact]
    public async Task AnArgumentThatWouldForgeACardLine_IsRefusedBeforeAnyCardIsRaised()
    {
        using (var scenario = new AssistantCreateProjectConsentTests())
        {
            await scenario._AssertAnArgumentThatWouldForgeACardLine_IsRefusedBeforeAnyCardIsRaisedAsync("Invoices\nfolder: /home/raymond/anything", null, null);
        }
        using (var scenario = new AssistantCreateProjectConsentTests())
        {
            await scenario._AssertAnArgumentThatWouldForgeACardLine_IsRefusedBeforeAnyCardIsRaisedAsync("Invoices", "/tmp/x\nfolder: /home/raymond/work/invoices", null);
        }
        using (var scenario = new AssistantCreateProjectConsentTests())
        {
            await scenario._AssertAnArgumentThatWouldForgeACardLine_IsRefusedBeforeAnyCardIsRaisedAsync("Invoices", null, "personal[2K");
        }
    }

    private async Task _AssertAnArgumentThatWouldForgeACardLine_IsRefusedBeforeAnyCardIsRaisedAsync(
        string name, string? sourceDirectory, string? defaultProfileLabel)
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools(_consent).CreateProjectAsync(name, sourceDirectory: sourceDirectory, defaultProfileLabel: defaultProfileLabel));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_consent.Asked);
        Assert.Empty(_gateway.Calls);
    }

    // ── behaviorPrompt/description are bounded, like send_message's body (AC-799 review finding 5) ────────────────



    // ── The rest of the shared plumbing every asking tool on this server carries ────────────────────────────────

    [Fact]
    public async Task ADeniedCard_NeverReachesTheGateway()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools(_consent).CreateProjectAsync("Invoices"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);
        Assert.Null(result["approval"]);
    }




    private sealed class RecordingBroker : IConsentBroker
    {
        public List<ConsentRequest> Asked { get; } = [];

        public bool Approve { get; set; } = true;

        public bool Bypassed { get; set; }

        public event EventHandler<ConsentPrompt>? PromptOpened;

        public event EventHandler<Guid>? PromptClosed;

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            PromptOpened?.Invoke(this, null!);
            PromptClosed?.Invoke(this, Guid.Empty);
            return Task.FromResult(new ConsentDecision(
                Approve ? ConsentOutcome.Approved : ConsentOutcome.Denied, Remembered: false, Bypassed: Approve && Bypassed));
        }

        public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
        {
        }
    }

    // Records rather than throws: the assertion that matters here is "the gateway was never reached", and a
    // throwing fake would be indistinguishable from the refusal it is meant to prove.
    private sealed class RecordingGateway : IAssistantAgentGateway
    {
        public List<string> Calls { get; } = [];

        // What `enabledMcpServerNames` actually carried on the most recent call — captured to prove the
        // `[]`-to-`null` normalisation (AC-799 review finding 1) happens before the gateway is reached, not only
        // on the card text.
        public IReadOnlyList<string>? LastEnabledMcpServerNames { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"CreateProjectAsync({name})");
            LastEnabledMcpServerNames = enabledMcpServerNames;
            return Task.FromResult(AssistantProjectCreateResult.Created("local-1", name));
        }

        public Task<AssistantProjectSnapshot?> GetProjectSnapshotAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            string? gitUrl = null,
            string? memoryRef = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
            string question, IReadOnlyList<(string Label, string? Description)> options, bool multiSelect, bool allowOther,
            string? header, CancellationToken cancellationToken = default)
        {
            Calls.Add($"AskStructuredQuestionAsync({question})");
            return Task.FromResult(AskStructuredQuestionResult.Shown());
        }

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
