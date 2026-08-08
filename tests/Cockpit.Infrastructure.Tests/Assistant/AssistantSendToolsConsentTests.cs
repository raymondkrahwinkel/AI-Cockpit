using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
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

    private static string _KeyOf(ConsentRequest request) =>
        ConsentSourceCatalog.KeyFor(request.Source.PluginId, request.Source.Label);

    // ── The two keys, which is what makes one switch unable to open both ───────────────────────────────────────

    /// <summary>
    /// AC-575 stores the operator's bypass per consent source, and a host-internal caller's key <em>is</em> its
    /// label. Two tools under one label would therefore be one row in Options and one switch — and "the assistant
    /// may leave notes unasked" would silently also mean "the assistant may start work unasked", which is the
    /// permissive reading of a question nobody was asked.
    /// </summary>
    [Fact]
    public async Task TheMessageAndThePrompt_AreAskedAboutUnderDifferentBypassKeys()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools().SendMessageAsync(TargetPane, "heads-up", "the branch moved");
        await _Tools().SendPromptAsync(TargetPane, "run the tests");

        Assert.Equal(2, _consent.Asked.Count);
        Assert.Equal(ConsentSourceCatalog.AssistantMessage, _KeyOf(_consent.Asked[0]));
        Assert.Equal(ConsentSourceCatalog.AssistantPrompt, _KeyOf(_consent.Asked[1]));
        Assert.NotEqual(_KeyOf(_consent.Asked[0]), _KeyOf(_consent.Asked[1]));
    }

    /// <summary>
    /// A key the operator cannot see is a key they cannot switch off again. Options lists
    /// <see cref="ConsentSourceCatalog.HostSources"/> plus whatever has already asked, so a source left out of that
    /// list only appears after it has interrupted them at least once.
    /// </summary>
    [Fact]
    public void BothKeys_AreOfferedInOptions_SoEitherCanBeAllowedOrRevokedOnItsOwn()
    {
        Assert.Contains(ConsentSourceCatalog.AssistantMessage, ConsentSourceCatalog.HostSources);
        Assert.Contains(ConsentSourceCatalog.AssistantPrompt, ConsentSourceCatalog.HostSources);
    }

    // ── The two weights ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMessage_IsLowRisk_AndAPrompt_IsDangerous()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools().SendMessageAsync(TargetPane, "heads-up", "the branch moved");
        await _Tools().SendPromptAsync(TargetPane, "run the tests");

        Assert.Equal(ConsentRisk.LowRisk, _consent.Asked[0].Risk);

        // ConsentRisk's own doc names "a session hand-off with the operator's rights" as the example of Dangerous,
        // and Dangerous is never remembered — so every single hand-off is its own click.
        Assert.Equal(ConsentRisk.Dangerous, _consent.Asked[1].Risk);
        Assert.False(_consent.Asked[1].AllowRemember);
    }

    // ── The card shows the truth ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ConsentRequest"/>'s own rule: <c>Action</c> is the literal thing that will run, never a
    /// caller-composed summary of it — because the caller here is a model whose words can be argued into being
    /// somebody else's. Both the text and the session that receives it have to be on the card, and the session is
    /// named by the pane id the cockpit will act on rather than by a name the assistant supplied.
    /// </summary>
    [Fact]
    public async Task ThePromptCard_ShowsTheProposedTurnWordForWord_AndWhichSessionGetsIt()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools().SendPromptAsync(TargetPane, "delete the release branch and force-push");

        var card = Assert.Single(_consent.Asked);
        Assert.Contains("delete the release branch and force-push", card.Action, StringComparison.Ordinal);
        Assert.Contains(TargetPane, card.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMessageCard_ShowsTheBodyAndKindWordForWord_AndWhichSessionGetsIt()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools().SendMessageAsync(TargetPane, "handover", "stop touching the worktree, I am rebasing it");

        var card = Assert.Single(_consent.Asked);
        Assert.Contains("stop touching the worktree, I am rebasing it", card.Action, StringComparison.Ordinal);
        Assert.Contains("handover", card.Action, StringComparison.Ordinal);
        Assert.Contains(TargetPane, card.Action, StringComparison.Ordinal);
    }

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
    }

    [Fact]
    public async Task ADeniedPrompt_NeverReachesTheSession()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools().SendPromptAsync(TargetPane, "run the tests"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);
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
                Approve ? ConsentOutcome.Approved : ConsentOutcome.Denied, false));
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

        public Task<AgentStopResult> StopAsync(string paneId, CancellationToken cancellationToken = default) =>
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
