using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

internal sealed class SwarmSessionHandle(NodeSessionRow snapshot) : ISessionHandle
{
    internal NodeSessionRow Snapshot { get; set; } = snapshot;
    public string PaneId => Snapshot.PaneId;
    public string Title => Snapshot.Name;
    public string WorkspaceId => "swarm";
    public string? PlacedWorkspaceId => WorkspaceId;
    public string? WorkingDirectory => null;
    public string? WorktreeBranch => null;
    public string? ActiveProfileLabel => Snapshot.Profile;
    public bool IsTerminal => false;
    public bool IsEmbedded => false;
    public SessionStatus SessionStatus => Snapshot.NeedsYou ? SessionStatus.NeedsAttention
        : Enum.TryParse<SessionStatus>(Snapshot.Status, true, out var status) ? status : SessionStatus.Idle;
    public string Statusline => Snapshot.Statusline;
    public bool CanTakeAPrompt => false;
    public bool DeliversInboxAtTurnStart => false;
    public bool HasPromptWaitingToBeDelivered => false;
    public bool HasPendingConsent => false;
    public int ProcessCount => 0;
    public double ProcessCpuPercent => 0;
    public long ProcessMemoryBytes => 0;
    public int AbandonedProcessCount => 0;
    public bool HasReadableTranscript => false;
    public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(Snapshot.HasOutstandingWork);
    public Task<SessionTranscriptSlice> ReadTranscriptAsync(int count) => Task.FromResult(new SessionTranscriptSlice([], 0));
    public Task<bool> SendPromptAsync(string prompt) => Task.FromResult(false);
    public Task<bool?> SubmitPromptWhenReadyAsync(string prompt) => Task.FromResult<bool?>(null);
    public Task SetWorktreeBranchAsync(string? branch) => Task.CompletedTask;
    public Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow, bool forSession = false) => Task.FromResult(false);
    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);
    public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync() => Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]);
    public Task<bool> SetStatuslineAsync(string statusline) => Task.FromResult(false);
    public Task<bool> SuggestNameAsync(string name) => Task.FromResult(false);
    public Task<SessionWakeState> ReadWakeStateAsync() => Task.FromResult(new SessionWakeState(false, SessionStatus, false));
}
