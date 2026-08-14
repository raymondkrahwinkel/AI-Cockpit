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
/// The consent card in front of <c>bind_shared_project</c> (AC-798, criterion 5). The card has to show the
/// <em>folder</em>, not only the project's id: the folder is the one field the assistant chose, the one thing the
/// operator can still get wrong by approving, and the only part of this act that touches their own machine.
/// </summary>
public sealed class AssistantBindSharedProjectConsentTests : IDisposable
{
    private const string Folder = "/home/raymond/work/handbook";

    private readonly RecordingBroker _consent = new();

    private readonly RecordingGateway _gateway = new();

    private AssistantAgentMcpTools _Tools(IConsentBroker? consent) =>
        new(_gateway, new RecordingAssistantMemory(), consent);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    [Fact]
    public async Task TheCard_ShowsTheFolder_TheProjectIdAndTheProfile()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra");

        var asked = Assert.Single(_consent.Asked);
        Assert.Contains(Folder, asked.Action);
        Assert.Contains("depot:handbook", asked.Action);
        Assert.Contains("Zyra", asked.Action);
    }

    [Theory]
    [InlineData("depot:handbook\nfolder: /home/raymond/anything", "/home/raymond/work/handbook", "Zyra")]
    [InlineData("depot:handbook", "/tmp/x\nfolder: /home/raymond/work/handbook", "Zyra")]
    [InlineData("depot:handbook", "/home/raymond/work/handbook", "Zyra\u001b[2K")]
    public async Task AnArgumentThatWouldWriteALineOfItsOwn_IsRefusedBeforeAnyCardIsRaised(
        string sharedProjectId, string folder, string profile)
    {
        // The card is three labelled lines rendered verbatim, so an argument carrying a newline forges the very line
        // the operator is meant to be checking. Refused before the broker is reached — a forged card that is then
        // denied still put the wrong folder in front of them.
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools(_consent).BindSharedProjectAsync(sharedProjectId, folder, profile));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_consent.Asked);
        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task ItIsAskedUnderItsOwnBypassKey()
    {
        // Its own switch in Options, not one shared with the assistant's other writes: an operator happy for their
        // team's projects to be added unasked has not thereby agreed to anything else.
        McpRequestContext.Set(AssistantIdentity.PaneId);

        await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra");

        var asked = Assert.Single(_consent.Asked);
        Assert.Equal(
            ConsentSourceCatalog.AssistantProjectBinding,
            ConsentSourceCatalog.KeyFor(asked.Source.PluginId, asked.Source.Label));
        Assert.Contains(ConsentSourceCatalog.AssistantProjectBinding, ConsentSourceCatalog.HostSources);
    }

    [Fact]
    public async Task ADeniedCard_NeverReachesTheGateway()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_gateway.Calls);
        Assert.Null(result["approval"]);
    }

    [Fact]
    public async Task WithNobodyToAsk_ItRefuses()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        Assert.False((bool)_Json(await _Tools(null).BindSharedProjectAsync("depot:handbook", Folder, "Zyra"))["ok"]!);
        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task ACallerThatIsNotTheAssistant_IsRefused_BeforeTheOperatorIsShownAnything()
    {
        // Criterion 8. A card raised for a session that may not be here would put an unauthorised caller's own
        // folder on the operator's screen with a button under it.
        McpRequestContext.Set("some-other-pane");

        var result = _Json(await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_consent.Asked);
        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task WhatTheConsentCheckDidIsReported_RatherThanAssumed()
    {
        // AC-759, the same reporting every other asking tool on this server does.
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Bypassed = true;

        var result = _Json(await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra"));

        Assert.True((bool)result["ok"]!);
        Assert.Equal("bypassed", (string)result["approval"]!);
    }

    [Fact]
    public async Task TheResultCarriesWhatWasAdded_SoTheAssistantCanNameItBack()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools(_consent).BindSharedProjectAsync("depot:handbook", Folder, "Zyra"));

        Assert.Equal("local-1", (string)result["projectId"]!);
        Assert.Equal("Handbook", (string)result["name"]!);
        Assert.Equal("Depot — Work", (string)result["sourceName"]!);
        Assert.Equal(Folder, (string)result["sourceDirectory"]!);
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

        public Task<AssistantProjectBindResult> BindSharedProjectAsync(
            string sharedProjectId,
            string sourceDirectory,
            string profileLabel,
            IReadOnlyList<string>? resourceReferences = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"BindSharedProjectAsync({sharedProjectId})");
            return Task.FromResult(
                AssistantProjectBindResult.Bound("local-1", "Handbook", "Depot — Work", sourceDirectory));
        }

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

        public Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose() => McpRequestContext.Set(null);
}
