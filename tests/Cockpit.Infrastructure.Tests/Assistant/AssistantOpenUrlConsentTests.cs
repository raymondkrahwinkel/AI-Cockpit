using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The consent gate in front of <c>open_url</c> (AC-587) — an egress surface reached from a prompt-injection path
/// (the assistant reads transcripts of other sessions), so what matters here is not merely that a card is raised,
/// but that it shows the literal address, is never skipped by an earlier approval, and lives under its own switch.
/// </summary>
public sealed class AssistantOpenUrlConsentTests : IDisposable
{

    private readonly RecordingBroker _consent = new();

    private readonly RecordingGateway _gateway = new();

    private AssistantAgentMcpTools _Tools(IConsentBroker? consent) =>
        new(_gateway, new RecordingAssistantMemory(), consent);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // ── Criterion 1: the card shows the full, literal URL ──────────────────────────────────────────────────────


    // ── Criterion 2: Dangerous, and never remembered ───────────────────────────────────────────────────────────


    // ── Criterion 3: its own source, so one switch cannot open this along with something else ─────────────────


    // ── Criterion 4: a non-web scheme is refused by the gateway, not re-decided here ───────────────────────────


    // ── Saying no, and having nobody to ask ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeniedOpen_NeverReachesTheGateway()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools(_consent).OpenUrlAsync("https://example.test"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);
        Assert.Null(result["approval"]);
    }


    // ── A newline would forge a line under the URL nobody approved ──────────────────────────────────────────────

    [Fact]
    public async Task AUrlCarryingAControlCharacter_IsRefused_BeforeAnyCardIsRaised()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools(_consent).OpenUrlAsync("https://example.test\nsource: trusted"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_consent.Asked);
        Assert.Empty(_gateway.Calls);
    }

    // ── AC-759: what the consent check actually did is reported, not assumed ──────────────────────────────────



    // ── The result names the URL that was actually opened ───────────────────────────────────────────────────────


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

    private sealed class RecordingGateway : IAssistantAgentGateway
    {
        public List<string> Calls { get; } = [];

        public OpenUrlResult NextResult { get; set; } = OpenUrlResult.Opened("https://example.test");

        public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls.Add($"OpenUrlAsync({url})");
            return Task.FromResult(NextResult);
        }

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

        public Task<AssistantProjectBindResult> BindSharedProjectAsync(
            string sharedProjectId,
            string sourceDirectory,
            string profileLabel,
            IReadOnlyList<string>? resourceReferences = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
            string? header, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose() => McpRequestContext.Set(null);
}
