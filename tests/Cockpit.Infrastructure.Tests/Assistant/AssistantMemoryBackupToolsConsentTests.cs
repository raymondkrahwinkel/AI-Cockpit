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
/// The consent gate in front of export_assistant_memory / import_assistant_memory (AC-657). Both ask before touching
/// disk, and are asked about separately — same reasoning as the message/prompt split: being fine with a copy of the
/// memory going out is not the same as being fine with the live memory being overwritten.
/// </summary>
public sealed class AssistantMemoryBackupToolsConsentTests : IDisposable
{
    private readonly RecordingBroker _consent = new();

    private readonly RecordingAssistantMemory _memory = new();

    private AssistantAgentMcpTools _Tools(IConsentBroker? consent) => new(new RefusingGateway(), _memory, consent);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    private static string _KeyOf(ConsentRequest request) =>
        ConsentSourceCatalog.KeyFor(request.Source.PluginId, request.Source.Label);

    [Fact]
    public async Task ExportAndImport_AreAskedAboutUnderDifferentBypassKeys()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false; // denied is fine — this is about which key each call is asked under, not the outcome.

        await _Tools(_consent).ExportAssistantMemoryAsync(@"C:\nowhere\export.zip");
        await _Tools(_consent).ImportAssistantMemoryAsync(@"C:\nowhere\export.zip");

        Assert.Equal(2, _consent.Asked.Count);
        Assert.Equal(ConsentSourceCatalog.AssistantMemoryExport, _KeyOf(_consent.Asked[0]));
        Assert.Equal(ConsentSourceCatalog.AssistantMemoryImport, _KeyOf(_consent.Asked[1]));
    }

    [Fact]
    public async Task Export_IsLowRisk_AndImport_IsDangerous()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        await _Tools(_consent).ExportAssistantMemoryAsync(@"C:\nowhere\export.zip");
        await _Tools(_consent).ImportAssistantMemoryAsync(@"C:\nowhere\export.zip");

        Assert.Equal(ConsentRisk.LowRisk, _consent.Asked[0].Risk);
        Assert.Equal(ConsentRisk.Dangerous, _consent.Asked[1].Risk);
    }

    [Fact]
    public async Task ADeniedImport_NeverTouchesDisk()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.Approve = false;

        var result = _Json(await _Tools(_consent).ImportAssistantMemoryAsync(@"C:\nowhere\export.zip"));

        Assert.False((bool)result["ok"]!);
    }

    [Fact]
    public async Task WithNobodyToAsk_BothToolsRefuse()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);

        Assert.False((bool)_Json(await _Tools(null).ExportAssistantMemoryAsync(@"C:\nowhere\export.zip"))["ok"]!);
        Assert.False((bool)_Json(await _Tools(null).ImportAssistantMemoryAsync(@"C:\nowhere\export.zip"))["ok"]!);
    }

    [Fact]
    public async Task ACallerThatIsNotTheAssistant_IsRefused_BeforeAnyConsentIsAsked()
    {
        McpRequestContext.Set("some-other-pane");

        var result = _Json(await _Tools(_consent).ExportAssistantMemoryAsync(@"C:\nowhere\export.zip"));

        Assert.False((bool)result["ok"]!);
        Assert.Empty(_consent.Asked);
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

    // These tools never call the gateway; a gateway that throws proves that.
    private sealed class RefusingGateway : IAssistantAgentGateway
    {
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
    }

    public void Dispose() => McpRequestContext.Set(null);
}
