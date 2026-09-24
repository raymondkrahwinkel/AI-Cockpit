using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

// AC-1373: one pane, SDK or TTY, as `ISessionRegistry` hands it out. Plain fields are read where the caller is, as
// `SessionWorkspaces` always did; anything that walks a UI-owned collection or acts takes the `UiThreadCall` route
// the gateways take today, with the same deadlines.
internal sealed class SessionPanelHandle(SessionPanelViewModel pane, bool isEmbedded, Func<string?> firstSessionsWorkspaceId)
    : ISessionHandle
{
    public string PaneId => pane.PaneId;

    public string Title => pane.Title;

    public string WorkspaceId => pane.WorkspaceId;

    public string? PlacedWorkspaceId => SessionWorkspacePlacement.Resolve(pane, firstSessionsWorkspaceId());

    public string? WorkingDirectory => pane.WorkingDirectory;

    public string? WorktreeBranch => pane.WorktreeBranch;

    public string? ActiveProfileLabel => pane.ActiveProfileLabel;

    public string? ProjectId => pane.ProjectId;

    // The flag the gateways filter on as "no agent here" is `ShowPluginHeaderItems`; a plain terminal is the only
    // pane that clears it, and it sets `IsTerminal` in the same breath (`TtyViewModel.LaunchTerminal`).
    public bool IsTerminal => !pane.ShowPluginHeaderItems;

    public bool IsEmbedded => isEmbedded;

    public SessionStatus SessionStatus => pane.SessionStatus;

    public string Statusline => pane.Statusline;

    public bool CanTakeAPrompt => pane.CanTakeAPrompt;

    public bool DeliversInboxAtTurnStart => pane.DeliversInboxAtTurnStart;

    public bool HasPromptWaitingToBeDelivered => pane.HasPromptWaitingToBeDelivered;

    // An SDK session answers this from its background-task list, which only the UI thread may walk.
    public Task<bool> HasOutstandingBackgroundShellsAsync() => UiThreadCall.RunAsync(() => pane.HasOutstandingBackgroundShells);

    public bool HasPendingConsent => pane.PendingConsent is not null;

    public int ProcessCount => pane.ProcessCount;

    public double ProcessCpuPercent => pane.ProcessCpuPercent;

    public long ProcessMemoryBytes => pane.ProcessMemoryBytes;

    public int AbandonedProcessCount => pane.AbandonedProcessCount;

    // The split `AssistantReadGateway.ReadTranscriptAsync` makes: an SDK transcript is sliced on the UI thread, a TTY
    // one is a file its CLI wrote and is read off it (AC-609). A plain terminal has written nothing.
    public async Task<SessionTranscriptSlice> ReadTranscriptAsync(int count) => pane switch
    {
        SessionViewModel sdk => await UiThreadCall.RunAsync(() => _SliceOf(sdk, count)).ConfigureAwait(false),
        TtyViewModel { IsTerminal: false } tty => await Task.Run(() => tty.ReadTranscriptEntries(count)).ConfigureAwait(false),
        _ => SessionTranscriptSlice.Empty,
    };

    // AC-294: a route, not content — false for a plain terminal, for a provider with no reader, and before a TTY's
    // pty is up. An SDK session always has one; it is in-memory rather than a file that could be missing.
    public bool HasReadableTranscript => pane switch
    {
        SessionViewModel => true,
        TtyViewModel tty => tty.HasReadableTranscript,
        _ => false,
    };

    public Task<bool> SendPromptAsync(string prompt) => UiThreadCall.RunAsync(() => pane.SendPromptAsync(prompt));

    // One dispatcher callback for the check and the hand-over: a pane still coming up holds exactly one brief, and a
    // second one arriving between the two would otherwise be held and misread as belonging to this call.
    public Task<bool?> SubmitPromptWhenReadyAsync(string prompt) =>
        UiThreadCall.RunAsync(() => pane.HasPromptWaitingToBeDelivered ? (bool?)null : pane.SubmitPromptWhenReady(prompt));

    public Task SetWorktreeBranchAsync(string? branch) => UiThreadCall.RunAsync(() => pane.WorktreeBranch = branch);

    // Only an SDK session holds permission prompts by tool-use id; a TTY session's are the CLI's own.
    public Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow) => pane is SessionViewModel sdk
        ? UiThreadCall.RunAsync(() => sdk.RespondToPermissionByIdAsync(toolUseId, allow))
        : Task.FromResult(false);

    // Always dispatched, as `SessionVerifyGateway` does (AC-577): every caller arrives off the UI thread.
    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) =>
        UiThreadCall.DispatchAsync(() => pane.FeedVerifyResultAsync(caption, screenshotPng));

    // Only an SDK session holds permission rows; a TTY session's prompts are its CLI's own, and a plain terminal has none.
    public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync() => pane is SessionViewModel sdk
        ? UiThreadCall.RunAsync(() => (IReadOnlyList<SessionPendingPermission>)
            [
                .. sdk.PendingToolPermissionRows().Select(row =>
                    new SessionPendingPermission(row.ToolUseId ?? "", row.ToolName ?? "", row.InputJson ?? "{}", row.Timestamp)),
            ])
        : Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]);

    public Task<bool> SetStatuslineAsync(string statusline) => UiThreadCall.RunAsync(() =>
    {
        pane.Statusline = statusline ?? string.Empty;
        return true;
    });

    public Task<bool> SuggestNameAsync(string name) => UiThreadCall.RunAsync(() => pane.SuggestName(name));

    // AC-1374: the three fields read as one dispatcher callback, so a wake decision never sees one from before a
    // change and another from after it — same deadline and UiUnavailable handling as every other hop here (AC-1138).
    public Task<SessionWakeState> ReadWakeStateAsync() =>
        UiThreadCall.RunAsync(() => new SessionWakeState(pane.PendingConsent is not null, pane.SessionStatus, pane.CanTakeAPrompt));

    private static SessionTranscriptSlice _SliceOf(SessionViewModel sdk, int count)
    {
        var transcript = sdk.Transcript;
        var skip = Math.Max(0, transcript.Count - count);
        return new SessionTranscriptSlice(
            [.. transcript.Skip(skip).Select(entry => new SessionTranscriptEntry(entry.Kind.ToString(), entry.TextWithImageSuffix, entry.ResultText))],
            transcript.Count);
    }
}
