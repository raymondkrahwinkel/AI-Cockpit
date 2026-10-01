using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

// AC-1450: what the desktop knows of an SDK pane before the pane exists — the facts it registers the handle with, and
// that the pane takes over when the registry's Changed makes it.
internal sealed record DesktopSessionLaunch(
    string PaneId, string Title, string? WorkspaceId, bool IsEmbedded, bool StartedByTheAssistant);

// AC-1439: how a pane the launcher hosts starts and stops; the cockpit's to do, on the UI thread.
internal sealed record HostedPaneCalls(
    Func<SessionPanelViewModel, SessionLaunchRequest, string?, Task> Start, Func<Task> Stop);

// AC-1373: one pane as `ISessionRegistry` hands it out. Plain fields are read where the caller is; anything that walks
// a UI-owned collection or acts takes `UiThreadCall`. AC-1392: an act asks `isLive` in that same callback, so a pane
// closed after the caller looked it up is left alone rather than written to.
internal sealed class SessionPanelHandle : IHostedSession
{
    private readonly DesktopSessionLaunch _launch;
    private readonly Func<string?> _firstSessionsWorkspaceId;
    private readonly Func<SessionPanelViewModel, bool>? _isLive;
    private SessionPanelViewModel? _pane;

    public SessionPanelHandle(
        SessionPanelViewModel pane, bool isEmbedded, Func<string?> firstSessionsWorkspaceId, Func<bool>? isLive = null)
    {
        _launch = new DesktopSessionLaunch(pane.PaneId, pane.Title, pane.WorkspaceId, isEmbedded, pane.StartedByTheAssistant);
        _firstSessionsWorkspaceId = firstSessionsWorkspaceId;
        _isLive = isLive is null ? null : _ => isLive();
        Attach(pane);
    }

    public SessionPanelHandle(
        DesktopSessionLaunch launch, ISessionControl control, Func<string?> firstSessionsWorkspaceId,
        Func<SessionPanelViewModel, bool>? isLive = null)
    {
        _launch = launch;
        Control = control;
        _firstSessionsWorkspaceId = firstSessionsWorkspaceId;
        _isLive = isLive;
    }

    // AC-1450: the session the desktop made and registered before its pane; until `Attach` the handle answers from its
    // launch facts. Null for a handle registered over a pane that already existed.
    public ISessionControl? Control { get; }

    public SessionPanelViewModel? Pane => _pane;

    // AC-1439: set on a pane the launcher hosts. Without it the pane was not started by the launcher, and a stop is a close.
    public HostedPaneCalls? Hosted { get; set; }

    // The launcher calls these once the pane has landed. Dispatched without the request-thread cap: a start takes as long
    // as its provider does, as it always did from the dialog.
    public Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default) =>
        _pane is SessionViewModel sdk ? _OnUiThreadAsync(() => sdk.PrepareRecordedTranscriptAsync(resume, cancellationToken)) : Task.CompletedTask;

    public Task StartAsync(SessionLaunchRequest request, string? workingDirectory) =>
        Hosted is { } hosted && _pane is { } pane
            ? _OnUiThreadAsync(() => hosted.Start(pane, request, workingDirectory))
            : throw new InvalidOperationException($"Pane '{PaneId}' has not landed, so it cannot start.");

    public Task StopAsync() => Hosted is { } hosted
        ? _OnUiThreadAsync(hosted.Stop)
        : UiThreadCall.RunAsync(() => _pane?.RequestSelfClose());

    private static Task _OnUiThreadAsync(Func<Task> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    public bool StartedByTheAssistant => _launch.StartedByTheAssistant;

    public string PaneId => _pane?.PaneId ?? _launch.PaneId;

    public string Title => _pane?.Title ?? _launch.Title;

    public string WorkspaceId => _pane?.WorkspaceId ?? _launch.WorkspaceId ?? string.Empty;

    public string? PlacedWorkspaceId => _pane is { } pane
        ? SessionWorkspacePlacement.Resolve(pane, _firstSessionsWorkspaceId())
        : _launch.WorkspaceId;

    public string? WorkingDirectory => _pane?.WorkingDirectory;

    public string? WorktreeBranch => _pane?.WorktreeBranch;

    public string? ActiveProfileLabel => _pane?.ActiveProfileLabel;

    public string? ProjectId => _pane?.ProjectId;

    // The flag the gateways filter on as "no agent here" is `ShowPluginHeaderItems`; a plain terminal is the only
    // pane that clears it, and it sets `IsTerminal` in the same breath (`TtyViewModel.LaunchTerminal`).
    public bool IsTerminal => _pane is { ShowPluginHeaderItems: false };

    public bool IsEmbedded => _launch.IsEmbedded;

    public SessionStatus SessionStatus => _pane?.SessionStatus ?? default;

    public string Statusline => _pane?.Statusline ?? string.Empty;

    public bool CanTakeAPrompt => _pane?.CanTakeAPrompt ?? false;

    public bool DeliversInboxAtTurnStart => _pane?.DeliversInboxAtTurnStart ?? false;

    public bool HasPromptWaitingToBeDelivered => _pane?.HasPromptWaitingToBeDelivered ?? false;

    // An SDK session answers this from its background-task list, which only the UI thread may walk.
    public Task<bool> HasOutstandingBackgroundShellsAsync() =>
        UiThreadCall.RunAsync(() => _pane?.HasOutstandingBackgroundShells ?? false);

    public bool HasPendingConsent => _pane?.PendingConsent is not null;

    public int ProcessCount => _pane?.ProcessCount ?? 0;

    public double ProcessCpuPercent => _pane?.ProcessCpuPercent ?? 0;

    public long ProcessMemoryBytes => _pane?.ProcessMemoryBytes ?? 0;

    public int AbandonedProcessCount => _pane?.AbandonedProcessCount ?? 0;

    // The split `AssistantReadGateway.ReadTranscriptAsync` makes: an SDK transcript is sliced on the UI thread, a TTY
    // one is a file its CLI wrote and is read off it (AC-609). A plain terminal has written nothing.
    public async Task<SessionTranscriptSlice> ReadTranscriptAsync(int count) => _pane switch
    {
        SessionViewModel sdk => await UiThreadCall.RunAsync(() => _SliceOf(sdk, count)).ConfigureAwait(false),
        TtyViewModel { IsTerminal: false } tty => await Task.Run(() => tty.ReadTranscriptEntries(count)).ConfigureAwait(false),
        _ => SessionTranscriptSlice.Empty,
    };

    // AC-294: a route, not content — false for a plain terminal, for a provider with no reader, and before a TTY's
    // pty is up. An SDK session always has one; it is in-memory rather than a file that could be missing.
    public bool HasReadableTranscript => Control is not null || _pane switch
    {
        SessionViewModel => true,
        TtyViewModel tty => tty.HasReadableTranscript,
        _ => false,
    };

    public Task<bool> SendPromptAsync(string prompt) =>
        UiThreadCall.RunAsync(() => _pane is { } pane ? pane.SendPromptAsync(prompt) : Task.FromResult(false));

    // One dispatcher callback for the check and the hand-over: a pane still coming up holds exactly one brief, and a
    // second one arriving between the two would otherwise be held and misread as belonging to this call.
    public Task<bool?> SubmitPromptWhenReadyAsync(string prompt) =>
        UiThreadCall.RunAsync(() => _pane is not { } pane || pane.HasPromptWaitingToBeDelivered ? (bool?)null : pane.SubmitPromptWhenReady(prompt));

    public Task SetWorktreeBranchAsync(string? branch) => UiThreadCall.RunAsync(() => _pane is { } pane ? pane.WorktreeBranch = branch : null);

    // Only an SDK session holds permission prompts by tool-use id; a TTY session's are the CLI's own.
    public Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow) => _pane is SessionViewModel sdk
        ? UiThreadCall.RunAsync(() => sdk.RespondToPermissionByIdAsync(toolUseId, allow))
        : Task.FromResult(false);

    // Always dispatched, as `SessionVerifyGateway` does (AC-577): every caller arrives off the UI thread.
    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => _pane is { } pane
        ? UiThreadCall.DispatchAsync(() => pane.FeedVerifyResultAsync(caption, screenshotPng))
        : Task.FromResult(false);

    // Only an SDK session holds permission rows; a TTY session's prompts are its CLI's own, and a plain terminal has none.
    public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync() => _pane is SessionViewModel sdk
        ? UiThreadCall.RunAsync(() => (IReadOnlyList<SessionPendingPermission>)
            [
                .. sdk.PendingToolPermissionRows().Select(row =>
                    new SessionPendingPermission(row.ToolUseId ?? "", row.ToolName ?? "", row.InputJson ?? "{}", row.Timestamp)),
            ])
        : Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]);

    public Task<bool> SetStatuslineAsync(string statusline) => _WhileLiveAsync(pane => pane.Statusline = statusline ?? string.Empty);

    public Task<bool> SuggestNameAsync(string name) => UiThreadCall.RunAsync(() => _IsLive() && _pane is { } pane && pane.SuggestName(name));

    public Task<bool> SetNameAsync(string name) => _WhileLiveAsync(pane => pane.SetNameDirectly(name));

    public Task<bool> InjectAndSubmitAsync(string text) => _WhileLiveAsync(pane => pane.InjectAndSubmit(text));

    public Task<bool> InsertTextAsync(string text) => _WhileLiveAsync(pane => pane.InjectText(text));

    // AC-1386: an SDK session's own rows, raised where its host raises them; a TTY pane has no host-owned transcript.
    public event Action<TranscriptRowUpsert>? RowUpserted
    {
        add
        {
            if ((Control ?? (_pane as SessionViewModel)?.Control) is { } control)
            {
                control.RowUpserted += value;
            }
        }

        remove
        {
            if ((Control ?? (_pane as SessionViewModel)?.Control) is { } control)
            {
                control.RowUpserted -= value;
            }
        }
    }

    // AC-1438: the host's fold as the pane last drew it, with its signals raised on the UI thread once drawn; a TTY
    // pane and a plain terminal keep none, as the contract's defaults say.
    public SessionLiveState LiveState => _pane is SessionViewModel sdk ? sdk.LiveState : SessionLiveState.None;

    // AC-1450: held here rather than on the pane, so a listener that arrives before the pane does is not lost.
    public event Action<SessionLiveState>? LiveStateChanged;

    public event Action<SessionTurnEnd>? TurnEnded;

    public event Action? ToolProgressed;

    public event Action<SessionBackgroundTaskNotice>? BackgroundTaskNotified;

    // AC-1450: once, on the UI thread, by whoever made the pane for this handle.
    public void Attach(SessionPanelViewModel pane)
    {
        if (_pane is not null)
        {
            throw new InvalidOperationException($"Pane '{PaneId}' already has its view.");
        }

        _pane = pane;
        if (pane is SessionViewModel sdk)
        {
            sdk.LiveStateChanged += state => LiveStateChanged?.Invoke(state);
            sdk.TurnEnded += end => TurnEnded?.Invoke(end);
            sdk.ToolActivity += () => ToolProgressed?.Invoke();
            sdk.BackgroundTaskNotified += notice => BackgroundTaskNotified?.Invoke(notice);
        }

        lock (_gate)
        {
            if (_outputTextProduced is not null)
            {
                pane.OutputTextProduced += _OnPaneOutputText;
            }

            if (_toolActivityProduced is not null)
            {
                pane.ToolActivityProduced += _OnPaneToolActivity;
            }
        }
    }

    // AC-1415: the pane's own signals, which stay their source on the desktop until AC-1437 moves them to the host. The
    // pane is hooked while anyone listens, so a handle nobody follows holds no handler on it.
    private readonly Lock _gate = new();
    private Action<string>? _outputTextProduced;
    private Action<SessionToolCall>? _toolActivityProduced;

    public event Action<string>? OutputTextProduced
    {
        add
        {
            lock (_gate)
            {
                if (_outputTextProduced is null && _pane is { } pane)
                {
                    pane.OutputTextProduced += _OnPaneOutputText;
                }

                _outputTextProduced += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _outputTextProduced -= value;
                if (_outputTextProduced is null && _pane is { } pane)
                {
                    pane.OutputTextProduced -= _OnPaneOutputText;
                }
            }
        }
    }

    public event Action<SessionToolCall>? ToolActivityProduced
    {
        add
        {
            lock (_gate)
            {
                if (_toolActivityProduced is null && _pane is { } pane)
                {
                    pane.ToolActivityProduced += _OnPaneToolActivity;
                }

                _toolActivityProduced += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _toolActivityProduced -= value;
                if (_toolActivityProduced is null && _pane is { } pane)
                {
                    pane.ToolActivityProduced -= _OnPaneToolActivity;
                }
            }
        }
    }

    public IReadOnlyList<ImageAttachment> CurrentTurnImages => _pane is { } pane
        ? [.. pane.CurrentTurnImages.Select(image => new ImageAttachment(image.MediaType, image.Base64Data))]
        : [];

    private void _OnPaneOutputText(object? sender, string text) => _outputTextProduced?.Invoke(text);

    private void _OnPaneToolActivity(object? sender, SessionToolActivity activity) =>
        _toolActivityProduced?.Invoke(new SessionToolCall(activity.PaneId, activity.ToolName, activity.InputJson, activity.ResultContent, activity.IsError));

    private bool _IsLive() => _pane is { } pane && (_isLive?.Invoke(pane) ?? true);

    private Task<bool> _WhileLiveAsync(Action<SessionPanelViewModel> act) => UiThreadCall.RunAsync(() =>
    {
        if (!_IsLive() || _pane is not { } pane)
        {
            return false;
        }

        act(pane);
        return true;
    });

    // AC-1374: the three fields read as one dispatcher callback, so a wake decision never sees one from before a
    // change and another from after it — same deadline and UiUnavailable handling as every other hop here (AC-1138).
    public Task<SessionWakeState> ReadWakeStateAsync() =>
        UiThreadCall.RunAsync(() => _pane is { } pane
            ? new SessionWakeState(pane.PendingConsent is not null, pane.SessionStatus, pane.CanTakeAPrompt)
            : new SessionWakeState(false, default, false));

    private static SessionTranscriptSlice _SliceOf(SessionViewModel sdk, int count)
    {
        var transcript = sdk.Transcript;
        var skip = Math.Max(0, transcript.Count - count);
        return new SessionTranscriptSlice(
            [.. transcript.Skip(skip).Select(entry => new SessionTranscriptEntry(entry.Kind.ToString(), entry.TextWithImageSuffix, entry.ResultText))],
            transcript.Count);
    }
}
