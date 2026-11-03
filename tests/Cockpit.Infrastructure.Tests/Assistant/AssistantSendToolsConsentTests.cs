using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The gate in front of the assistant's two reaching-into-someone-else's-session tools. Telling an agent something
/// and making it do something are two tools on purpose, and these cover the three things that separation rests on:
/// they are asked about under two different keys, they are asked at two different weights, and what the operator
/// reads on the card is the literal text that will be delivered rather than the assistant's account of it.
/// </summary>
public sealed class AssistantSendToolsConsentTests : IDisposable
{
    private const string TargetPane = "pane-worker";

    private readonly RecordingBroker _consent = new();

    private readonly RecordingGateway _gateway = new();

    private readonly RecordingAssistantMemory _memory = new();

    private AssistantAgentMcpTools _Tools() => new(_gateway, _memory, _consent);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;


    // ── The two keys, which is what makes one switch unable to open both ───────────────────────────────────────



    // ── The two weights ────────────────────────────────────────────────────────────────────────────────────────


    // ── The card shows the truth ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ConsentRequest"/>'s own rule: <c>Action</c> is the literal thing that will run, never a
    /// caller-composed summary of it — because the caller here is a model whose words can be argued into being
    /// somebody else's. Both the text and the session that receives it have to be on the card, and the session is
    /// named by the pane id the cockpit will act on rather than by a name the assistant supplied.
    /// </summary>


    /// <summary>
    /// The card and the inbox must carry the same bytes. Terminal control sequences are stripped from a message
    /// before it is delivered, so a card built from the raw argument would be showing the operator something other
    /// than what arrives — and a card showing more than what arrives is as wrong as one showing less.
    /// </summary>
    [Fact]
    public async Task TheMessageCard_ShowsWhatWillActuallyBeDelivered_NotTheRawArgument()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        // Written as a code rather than an escape, so no invisible character ends up in this file.
        const char Escape = (char)0x1b;
        await _Tools().SendMessageAsync(TargetPane, "heads-up", $"before{Escape}[2Jafter");

        var card = Assert.Single(_consent.Asked);
        var delivered = Assert.Single(_gateway.Messages);

        // The escape byte itself is what a recipient's terminal would act on, so that is what goes — the printable
        // remainder is left alone, which is the same normalisation notify applies.
        Assert.Equal("before[2Jafter", delivered.Body);
        Assert.Contains(delivered.Body, card.Action, StringComparison.Ordinal);
        Assert.DoesNotContain(Escape, card.Action);
    }

    // ── Saying no, and having nobody to ask ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeniedMessage_NeverReachesTheInbox()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools().SendMessageAsync(TargetPane, "heads-up", "the branch moved"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);

        // AC-759 criterion 6: a refusal carries its reason and nothing that could read as a half-formed approval.
        Assert.Null(result["approval"]);
    }

    [Fact]
    public async Task ADeniedPrompt_NeverReachesTheSession()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools().SendPromptAsync(TargetPane, "run the tests"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);
        Assert.Null(result["approval"]);
    }

    /// <summary>
    /// No broker means no operator to ask. Both of these deliver into a session the assistant did not start, so the
    /// answer is no — not "carry on because nobody objected".
    /// </summary>
    [Fact]
    public async Task WithNobodyToAsk_BothToolsRefuse_AndNeitherReachesTheGateway()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var tools = new AssistantAgentMcpTools(_gateway, _memory);

        Assert.False((bool)_Json(await tools.SendMessageAsync(TargetPane, "heads-up", "the branch moved"))["ok"]!);
        Assert.False((bool)_Json(await tools.SendPromptAsync(TargetPane, "run the tests"))["ok"]!);
        Assert.Empty(_gateway.Calls);
    }

    // ── AC-759: what the consent check actually did is reported, not assumed ─────────────────────────────────────

    /// <summary>
    /// AC-759 criterion 2: a card that was actually raised and clicked reports "asked" — the state a fresh
    /// install is not in by default (<see cref="AssistantSettings.ConsentBypassAll"/>), but the one every tool
    /// description's caveat still promises can happen.
    /// </summary>

    /// <summary>
    /// AC-759 criterion 1: the default install (<c>ConsentBypassAll</c> true) never raises a card at all — the
    /// broker's decision carries <see cref="ConsentDecision.Bypassed"/>, and that is what the tool reports rather
    /// than the generic "it went through" an unconditional caveat used to leave the assistant to guess at.
    /// </summary>

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

        public List<(string PaneId, string Kind, string Body)> Messages { get; } = [];

        public List<(string PaneId, string Prompt)> Prompts { get; } = [];

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

        public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default) =>
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

        public Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SendMessageAsync({paneId})");
            Messages.Add((paneId, kind, body));
            return Task.FromResult(AgentMessageResult.Sent(paneId, "worker", "msg-1", deduplicated: false, deliversAtTurnStart: true));
        }

        public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SendPromptAsync({paneId})");
            Prompts.Add((paneId, prompt));
            return Task.FromResult(AgentPromptResult.Handed(paneId, "worker", delivered: true));
        }
    }

    public void Dispose() => McpRequestContext.Set(null);
}
