using System.Text;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-2288: a TTY session the server runs itself: the provider's TUI in a server pty, launched by the same `ITtyLauncher`
// a desktop pane uses, so profile, MCP and the composed launch options are the server's. A remote window reads and
// types through `TerminalEndpoints`; the turn is the TUI's own, so the host-owned turn members stay empty (AC-294).
internal sealed class BackendTtySession(
    SessionHostingRequest hosting,
    ITtyLauncher launcher,
    ITtySessionProviderResolver providers,
    ISessionResourceResolver? resources = null) : IHostedSession, IAsyncDisposable
{
    private const short StartColumns = 120;
    private const short StartRows = 40;

    private string _title = hosting.Name;
    private string _statusline = string.Empty;
    private string? _worktreeBranch = hosting.WorktreeBranch;

    public ServerPty? Pty { get; private set; }

    public string PaneId => hosting.PaneId;

    public string Title => Volatile.Read(ref _title);

    public string WorkspaceId => hosting.Request.WorkspaceId;

    public string? PlacedWorkspaceId => WorkspaceId;

    public string? WorkingDirectory => hosting.WorkingDirectory;

    public string? WorktreeBranch => Volatile.Read(ref _worktreeBranch);

    public string? ActiveProfileLabel => hosting.Request.Profile.Label;

    public Core.Profiles.SessionProfile? ActiveProfile => hosting.Request.Profile;

    public string? ProjectId => hosting.Request.ProjectId;

    public bool StartedByTheAssistant => hosting.Request.StartedByTheAssistant;

    public bool IsTerminal => false;

    public bool IsEmbedded => false;

    public bool AwaitsStart => Pty is null;

    public SessionStatus SessionStatus => Pty is { HasExited: false } ? SessionStatus.Busy : SessionStatus.Idle;

    public string Statusline => Volatile.Read(ref _statusline);

    public bool CanTakeAPrompt => Pty is { HasExited: false };

    public bool DeliversInboxAtTurnStart => false;

    public bool HasPromptWaitingToBeDelivered => false;

    public bool HasPendingConsent => false;

    public int ProcessCount => 0;

    public double ProcessCpuPercent => 0;

    public long ProcessMemoryBytes => 0;

    public int AbandonedProcessCount => 0;

    public bool HasReadableTranscript => false;

    public async Task StartAsync(SessionLaunchRequest request, string? workingDirectory)
    {
        var provider = providers.Resolve(request.Profile)
            ?? throw new InvalidOperationException($"{request.Profile.Label} has no terminal interface on this server.");

        // What the plugins give a starting session, as an SDK session's driver asks them (AC-165, AC-294).
        var contributed = resources is null ? null : await resources.ResolveAsync(PaneId, request.EnabledMcpServerNames).ConfigureAwait(false);
        var process = launcher.Launch(
            provider, request.Profile, request.LaunchOptions ?? new Dictionary<string, string>(), StartColumns, StartRows,
            workingDirectory, request.Resume, PaneId, request.EnabledMcpServerNames, contributed, request.ProjectId);
        Pty = new ServerPty(process);
    }

    public Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(false);

    public Task<SessionTranscriptSlice> ReadTranscriptAsync(int count) => Task.FromResult(SessionTranscriptSlice.Empty);

    // Typed into the TUI the way the desktop's pane types a prompt: the text, then Enter.
    public async Task<bool> SendPromptAsync(string prompt)
    {
        if (Pty is not { HasExited: false } pty || string.IsNullOrEmpty(prompt))
        {
            return false;
        }

        await pty.WriteAsync(Encoding.UTF8.GetBytes(prompt.ReplaceLineEndings("\n") + "\r"), CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    // The TUI says nothing about being ready, so a first prompt is not typed blind into a starting process.
    public Task<bool?> SubmitPromptWhenReadyAsync(string prompt) => Task.FromResult<bool?>(false);

    public Task SetWorktreeBranchAsync(string? branch)
    {
        Volatile.Write(ref _worktreeBranch, branch);
        return Task.CompletedTask;
    }

    public Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow, bool forSession = false) => Task.FromResult(false);

    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

    public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync() =>
        Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]);

    public Task<bool> SetStatuslineAsync(string statusline)
    {
        Volatile.Write(ref _statusline, statusline ?? string.Empty);
        return Task.FromResult(true);
    }

    public Task<bool> SuggestNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || hosting.NameIsChosen)
        {
            return Task.FromResult(false);
        }

        Volatile.Write(ref _title, name.Trim());
        return Task.FromResult(true);
    }

    public Task<SessionWakeState> ReadWakeStateAsync() =>
        Task.FromResult(new SessionWakeState(false, SessionStatus, CanTakeAPrompt, TypesIntoATerminal: true));

    public ValueTask DisposeAsync() => Pty?.DisposeAsync() ?? ValueTask.CompletedTask;
}
