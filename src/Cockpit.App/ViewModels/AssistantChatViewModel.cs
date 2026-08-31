using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface this window needs from the assistant's owning host (AC-543). The lead's <c>AssistantSessionHost</c>
/// already carries exactly this shape but as a sealed class with no interface, heavy to construct directly.
/// Extracted purely for that; the one remaining step is a one-line <c>: IAssistantSessionHost</c> on that class.
/// </summary>
public interface IAssistantSessionHost : INotifyPropertyChanged
{
    /// <summary>
    /// The assistant's own long-running session, or null while it has not been lazily started yet.
    /// </summary>
    SessionViewModel? Session { get; }

    /// <summary>
    /// Turns speaking on or off on the live session, so the header toggle reaches the next reply and not only the
    /// one that is playing. A no-op while nothing has been started yet — the value is read again at the next start.
    /// </summary>
    void SetSpeakReplies(bool speak);

    /// <summary>
    /// What the indicator shows; pairs with <see cref="UnavailableReason"/> when it is <see cref="AssistantActivity.Unavailable"/>.
    /// </summary>
    AssistantActivity Activity { get; }

    /// <summary>
    /// Why the assistant cannot be reached right now (feature off, no profile, failed start) — null otherwise.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Idempotent lazy start: returns the running session if there is one, restarts it if it fell over, and no-ops (leaving <see cref="Activity"/> at Unavailable) if the feature is off or the slot is empty.
    /// </summary>
    Task<SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stands the running assistant down and brings it back up on the same conversation, so a setting that can
    /// only be chosen at a start actually gets one. Starts it if nothing is running yet.
    /// </summary>
    Task<SessionViewModel?> RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the visible conversation and starts the assistant on a fresh one.
    /// </summary>
    Task<SessionViewModel?> ClearConversationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a clear for after the current turn and reports whether one was already queued.
    /// </summary>
    ClearConversationResult RequestConversationClear() =>
        ClearConversationResult.Refused("This host cannot queue a conversation clear.");

    /// <summary>
    /// Sends typed or spoken text to the assistant, starting it lazily first if it has not run yet.
    /// </summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same with images attached (AC-1049), already validated and PNG-encoded by the caller. They ride on the
    /// message the text sends, or on a message of their own when the text is empty.
    /// </summary>
    /// <remarks>
    /// Default-implemented so the test doubles of this interface that never send an image stay as they are. There
    /// is one real implementation by design (see <c>AssistantSessionHost</c>) and it overrides this.
    /// </remarks>
    Task SendAsync(string text, IReadOnlyList<byte[]> pngImages, CancellationToken cancellationToken = default) =>
        SendAsync(text, cancellationToken);

    /// <summary>
    /// AC-740: the Assistant Profile's own default working directory, once known — read synchronously so the
    /// @-mention picker can fall back to it before any <see cref="Session"/> exists. Null until the profile has
    /// loaded at least once; a read lazily triggers that load, so a window that never opens the picker never pays for it.
    /// </summary>
    string? DefaultWorkingDirectory { get; }

    /// <summary>
    /// Re-reads the settings and stands the assistant down if the feature was switched off — mid-sentence
    /// included, which is the point of it being a separate call rather than something checked on the next use.
    /// </summary>
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The assistant hotkey went down or came back up, so the assistant is (or is no longer) the one listening.
    /// Told, not inferred — the indicator used to read "who is listening" off the shared voice pill, so holding
    /// the assistant key lit it up as <em>dictation</em> instead. The coordinator now says so directly.
    /// </summary>
    void ReportHoldListening(bool listening);

    /// <summary>
    /// Speech-to-text is turning the words into text, or has finished doing so. Told for the same reason as
    /// <see cref="ReportHoldListening"/>: the coordinator that ran the hold knows, and the chip must not have to
    /// guess it off a signal that dictation writes to as well.
    /// </summary>
    void ReportTranscribing(bool transcribing);

    /// <summary>
    /// Speech-to-text is fetching what it needs before it can transcribe — <paramref name="status"/> names the
    /// step ("Downloading speech model") and <paramref name="fraction"/> is 0..1 where a total is known, null
    /// where the stream carries no length. A null <paramref name="status"/> ends the preparation.
    /// </summary>
    void ReportPreparing(string? status, double? fraction);
}

// Backs the pop-out chat window (AC-543 criteria 7, 8, 9): a peephole onto the assistant's own standing conversation,
// never its owner (AC-575).
public sealed partial class AssistantChatViewModel : ObservableObject, IDisposable
{
    private readonly IAssistantSessionHost _host;
    private readonly IAssistantSettingsStore _settingsStore;
    private readonly IVoicePlaybackQueue _playbackQueue;

    // AC-662: the sidebar's always-on/listen switch (AssistantIndicatorCoordinator.Indicator), fed in so the header can
    // carry the same toggle without a second copy of its state, its command, or its one-time cost confirmation.
    private readonly AssistantIndicatorViewModel? _indicator;

    // Making the spawn trail a fourth required one would be a compile break in every one of them for a flyout most of
    // those paths never open (AC-545).
    private readonly IAssistantSpawnAuditLog? _spawnAuditLog;

    // AC-740: null in the design-time/unit-test graph, where the @-mention picker's file source always answers empty.
    private readonly IMentionFileSource? _mentionFileSource;

    // AC-776: optional so every construction path predating this ticket (tests, Screenshotter) keeps compiling.
    // The one thing this view model reaches for outside its own host — see LiveSessions' remarks for why a whole
    // CockpitViewModel rather than a narrower gateway.
    private readonly CockpitViewModel? _cockpit;

    private IReadOnlyDictionary<string, string> _deskNameByPaneId = new Dictionary<string, string>(StringComparer.Ordinal);

    // Session is exposed only through the property below, not as [ObservableProperty] on a field, because it is never
    // assigned locally — it always reads straight through to the host.
    private SessionViewModel? _observedSession;

    // Set while _LoadSpeakRepliesAsync is applying a freshly loaded value to SpeakReplies, so that assignment does not
    // read as the operator flicking the switch: without this guard, a stored "off" would fire the same
    // playback-interrupt and re-save that a real click does, on every window open, for a value that was never touched.
    private bool _loadingSpeakReplies;

    // Fix found while verifying AC-935: without this, CanSend never re-evaluated and the Send button
    // stayed at whatever it read on the first render (grey) — unnoticed because Enter bypasses it and
    // checks CanExecute directly. See `_OnPendingAttachmentsChanged` for CanSend's other dependency.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _inputText = string.Empty;

    // Mirrors AssistantSettings.SpeakReplies's own default (true) until the real value loads, so the header does
    // not flash "off" for the one frame before EnsureOpenedAsync's load completes.
    [ObservableProperty]
    private bool _speakReplies = true;

    // The second half is what makes this true rather than merely usually true (AC-575).
    [ObservableProperty]
    private bool _consentBypassActive;

    // Mirrors `AssistantSettings.AlwaysOnTop` (AC-681) — Options-only, no write-back from this window. Read
    // alongside `SpeakReplies` on open and on every `ApplySettingsAsync`, so a change while the window is
    // already sitting there takes effect without a reopen.
    [ObservableProperty]
    private bool _alwaysOnTop = true;

    // AC-952: which host the chat view is sitting in — the floating window, or AC-953's dock rail. The header
    // reads it to swap Close for Undock rather than carrying two copies of its whole button row.
    [ObservableProperty]
    private bool _isDocked;

    // Everything else the operator had in flight (input text, attachments, the mention picker) is already on this view
    // model, and the transcript comes from the session: the scroll offset is the only thing a fresh view per host would
    // otherwise lose (AC-953).
    internal TranscriptScrollPosition? TranscriptAnchor { get; set; }

    // Raised by the header's Dock/Undock button. The view model cannot do the swap itself — which hosts exist is
    // `AssistantIndicatorCoordinator`'s business, and it is what builds this — so it only says that it was asked for.
    public event EventHandler? DockToggleRequested;

    [RelayCommand]
    private void ToggleDock() => DockToggleRequested?.Invoke(this, EventArgs.Empty);

    // Mirrors `AssistantSettings.PushToTalkKeyName` (AC-671), same read points as `AlwaysOnTop` above. Backs the
    // composer's placeholder instead of a hardcoded "F10" — see `_LoadSpeakRepliesAsync` for the empty-settings
    // fallback.
    [ObservableProperty]
    private string _pushToTalkKeyName = "F10";

    public AssistantChatViewModel(
        IAssistantSessionHost host,
        IAssistantSettingsStore settingsStore,
        IVoicePlaybackQueue playbackQueue,
        IAssistantSpawnAuditLog? spawnAuditLog = null,
        AssistantIndicatorViewModel? indicator = null,
        IMentionFileSource? mentionFileSource = null,
        CockpitViewModel? cockpit = null)
    {
        _host = host;
        _settingsStore = settingsStore;
        _playbackQueue = playbackQueue;
        _spawnAuditLog = spawnAuditLog;
        _indicator = indicator;
        _mentionFileSource = mentionFileSource;
        _cockpit = cockpit;
        MentionPicker = new MentionPickerViewModel(_MentionPathsAsync, () => Session?.WorkingDirectory ?? _host.DefaultWorkingDirectory);
        _observedSession = _host.Session;
        _WatchTranscript(previous: null, _observedSession);
        _host.PropertyChanged += _OnHostPropertyChanged;

        if (_cockpit is not null)
        {
            _cockpit.Sessions.CollectionChanged += _OnCockpitSessionsChanged;
            _RebuildLiveSessions();
        }
    }

    // AC-740: null source (design-time/unit-test graph) or no working directory yet (no session started, and the
    // Assistant Profile's own default not loaded/set either) both answer empty rather than throw.
    private Task<IReadOnlyList<string>> _MentionPathsAsync(CancellationToken cancellationToken)
    {
        var workingDirectory = Session?.WorkingDirectory ?? _host.DefaultWorkingDirectory;
        return _mentionFileSource is not null && workingDirectory is not null
            ? _mentionFileSource.GetPathsAsync(workingDirectory, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);
    }

    // Read-through, like `Session` below: never assigned locally, only ever reports whatever `_indicator` holds.
    public AssistantIndicatorViewModel? Indicator => _indicator;

    // The assistant's own session, bound straight through to the existing SDK transcript view.
    public SessionViewModel? Session => _host.Session;

    // Whether there is anything to show yet — a fresh install, or a window opened before the first message, both read
    // as "no session" rather than an empty transcript on a phantom one.
    public bool HasSession => Session is not null;

    // AC-740: the @-mention picker, shared shape with SessionView's via MentionPickerViewModel. This window's
    // working directory has two sources, tried in order — the live session's own, then the Assistant Profile's
    // default before any session exists — never the Cockpit process's own cwd.
    public MentionPickerViewModel MentionPicker { get; }

    // Whether the transcript has any rows — separate from `HasSession` because a session can exist (started) with
    // nothing said in it yet.
    public bool HasMessages => Session?.HasTranscript ?? false;

    // True while the assistant cannot be reached at all (criterion 1: feature off, no profile, or a failed start) —
    // paired with `UnavailableReason` so the window says why instead of just sitting empty.
    public bool IsUnavailable => _host.Activity == AssistantActivity.Unavailable;

    public string? UnavailableReason => _host.UnavailableReason;

    // An image with no words is a message too (AC-630) — the same rule `SessionViewModel.CanSend` applies, or a
    // pasted image would sit in the strip with no way to send it. Read live off the session on each CanExecute, so
    // nothing has to be notified when the attachment strip changes.
    public bool CanSend => !string.IsNullOrWhiteSpace(InputText) || Session is { HasPendingAttachments: true };

    // The spawn trail's most recent entries, newest first, for the flyout's `ItemsControl` — see `LoadSpawnLogAsync`
    // for why the trail and not the transcript is what answers "what has this thing ever started".
    public ObservableCollection<AssistantSpawnLogRowViewModel> SpawnLogEntries { get; } = new();

    // Raised by hand in `LoadSpawnLogAsync` — the load runs once per open rather than being observed continuously, so
    // there is nothing to watch.
    public bool HasSpawnLogEntries => SpawnLogEntries.Count > 0;

    // AC-776: every live agent session, filtered like AssistantReadGateway._ListSessions and rebuilt on
    // start/stop — real SessionPanelViewModel instances, not a DTO, so the header binds to them directly.
    public ObservableCollection<SessionPanelViewModel> LiveSessions { get; } = new();

    // Whether the session pill has anything to show — its own flag (AC-776 pitfall 3), not borrowed from the
    // usage pill's `HasUsagePillRegion`: a session badge must not disappear just because the assistant itself has
    // not reported usage yet.
    public bool HasLiveSessions => LiveSessions.Count > 0;

    // AC-776: resolved once per rebuild via AssistantReadGateway._ListSessions's own workspace-lookup — see
    // the ticket for why this isn't a property on SessionPanelViewModel itself.
    public IReadOnlyDictionary<string, string> DeskNameByPaneId
    {
        get => _deskNameByPaneId;
        private set => SetProperty(ref _deskNameByPaneId, value);
    }

    // AC-895: a session badge's click reuses the one existing "focus this session" command rather than a new
    // path — this is a thin passthrough so the AXAML template (bound to AssistantChatViewModel, not
    // CockpitViewModel) can reach it.
    [RelayCommand]
    private void SelectSession(SessionPanelViewModel session) => _cockpit?.SelectSessionCommand.Execute(session);

    private void _OnCockpitSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _RebuildLiveSessions();

    private void _RebuildLiveSessions()
    {
        if (_cockpit is null)
        {
            return;
        }

        var firstWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(_cockpit.Workspaces.Settings);
        var namesById = _cockpit.Workspaces.Settings.Workspaces.ToDictionary(
            workspace => workspace.Id, workspace => workspace.Name, StringComparer.Ordinal);

        var sessions = _cockpit.AllSessions()
            .Where(session => session.ShowPluginHeaderItems
                && !string.Equals(session.PaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
            .ToList();

        LiveSessions.Clear();
        foreach (var session in sessions)
        {
            LiveSessions.Add(session);
        }

        DeskNameByPaneId = sessions.ToDictionary(
            session => session.PaneId,
            session =>
            {
                var workspaceId = SessionWorkspacePlacement.Resolve(session, firstWorkspaceId);
                return workspaceId is not null && namesById.TryGetValue(workspaceId, out var name) ? name : workspaceId ?? "—";
            },
            StringComparer.Ordinal);

        OnPropertyChanged(nameof(HasLiveSessions));
    }

    // Never called from the constructor: a view model built to seed design-time/Screenshotter data must not reach out
    // and start a real session the moment it exists.
    public async Task EnsureOpenedAsync(CancellationToken cancellationToken = default)
    {
        await _LoadSpeakRepliesAsync(cancellationToken).ConfigureAwait(true);
        await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(true);
    }

    // Re-reads the settings this window mirrors, for an Options save that landed while it was open — the same
    // saved signal the chip already follows (`AssistantPushToTalkCoordinator._OnSettingsSavedAsync` →
    // `AssistantIndicatorCoordinator.ApplySettingsAsync`), so the two cannot disagree about the bypass mark.
    public Task ApplySettingsAsync(CancellationToken cancellationToken = default) =>
        _LoadSpeakRepliesAsync(cancellationToken);

    private async Task _LoadSpeakRepliesAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        // AC-575, criterion 5. The window where the assistant's actions are read is also where "some of these were
        // never shown to you" has to be legible; the chip carries the same mark for when this window is closed.
        ConsentBypassActive = settings.HasConsentBypass;
        AlwaysOnTop = settings.AlwaysOnTop;
        PushToTalkKeyName = string.IsNullOrWhiteSpace(settings.PushToTalkKeyName) ? "F10" : settings.PushToTalkKeyName;

        _loadingSpeakReplies = true;
        try
        {
            SpeakReplies = settings.SpeakReplies;
        }
        finally
        {
            _loadingSpeakReplies = false;
        }
    }

    // Guarded by `_loadingSpeakReplies` so applying a freshly loaded value on open never reads as a click.
    partial void OnSpeakRepliesChanged(bool value)
    {
        if (_loadingSpeakReplies)
        {
            return;
        }

        if (!value)
        {
            // Whoever clicks off wants silence, not one more paragraph — cut what is already playing before the
            // setting even finishes persisting, so there is no window where the old value lingers audibly.
            _playbackQueue.StopAll();
        }

        // And the next reply follows too. Stopping the current one was all this did, which made the toggle look
        // like a mute button for one paragraph: turned back on, nothing was ever spoken again, because the live
        // session's own read-aloud flag was never the thing being switched.
        _host.SetSpeakReplies(value);

        _ = _PersistSpeakRepliesAsync(value);
    }

    private async Task _PersistSpeakRepliesAsync(bool value)
    {
        // Read-modify-write on the whole record: IAssistantSettingsStore persists AssistantSettings as a unit, and
        // this window only ever means to change the one field, never to clobber whatever Options last saved for
        // IsEnabled/ListeningMode/etc. with values already loaded here.
        var current = await _settingsStore.LoadAsync().ConfigureAwait(true);
        await _settingsStore.SaveAsync(current with { SpeakReplies = value }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 && Session is not { HasPendingAttachments: true })
        {
            return;
        }

        InputText = string.Empty;
        // Goes through the host, not Session.SendCommand: Session can still be null here (nothing typed yet since
        // this instance came up), and the host's SendAsync is what performs the lazy start — the first message
        // typed into an unstarted assistant is exactly what starts it (criterion 1).
        await _host.SendAsync(text);
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    // AC-942: stops the current turn and, unlike SessionViewModel.StopAsync itself, also cuts a reply already
    // being read aloud — kept here rather than on the shared VM so a plain session panel's Stop stays untouched.
    [RelayCommand]
    private async Task StopAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        _playbackQueue.StopAll();
        await session.StopCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ClearConversationAsync()
    {
        if (_cockpit is null || !await _cockpit.ConfirmAsync(
                "Clear conversation?",
                "The visible conversation will disappear from this window, but remains available in transcripts/. The assistant's memory and current-state note stay intact.",
                "Clear conversation"))
        {
            return;
        }

        await StopAsync();
        await _host.ClearConversationAsync();
    }

    // Arrow-Up recall (AC-630), bridged: `SessionViewModel.RecallLastQueuedMessage` puts the text back in the
    // session's own composer, which this window does not show — so it is moved into the box that is on screen.
    // False on an empty queue, so the key handler can let Arrow-Up do its normal thing.
    public bool RecallLastQueuedMessage()
    {
        if (Session is not { } session || !session.RecallLastQueuedMessage())
        {
            return false;
        }

        InputText = session.InputText;
        session.InputText = string.Empty;
        return true;
    }

    // Reads the spawn trail back for the flyout (AC-545 criterion 5), called only when the flyout actually opens
    // (`AssistantChatWindow._OnSpawnLogFlyoutOpened`) rather than every time this window does.
    [RelayCommand]
    private async Task LoadSpawnLogAsync()
    {
        if (_spawnAuditLog is null)
        {
            return;
        }

        var entries = await Task.Run(() => _spawnAuditLog.ReadRecentAsync()).ConfigureAwait(true);

        SpawnLogEntries.Clear();
        foreach (var entry in entries)
        {
            SpawnLogEntries.Add(AssistantSpawnLogRowViewModel.From(entry));
        }

        OnPropertyChanged(nameof(HasSpawnLogEntries));
    }

    // Written here rather than in the view so it can be tested, and deliberately dumb: every row in the order it
    // happened, labelled by what it is, tool results included.
    public string TranscriptAsText()
    {
        if (Session is not { } session)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder()
            .AppendLine($"Cockpit assistant conversation — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}")
            .AppendLine();

        foreach (var entry in session.Transcript)
        {
            var label = entry.Kind switch
            {
                TranscriptEntryKind.UserText => "You",
                TranscriptEntryKind.AssistantText => "Assistant",
                TranscriptEntryKind.Thinking => "Thinking",
                TranscriptEntryKind.ToolUse => "Tool",
                TranscriptEntryKind.ToolResult => "Tool result",
                var other => other.ToString(),
            };

            text.AppendLine($"[{label}] {entry.Text}");
            if (entry.ResultText is { Length: > 0 } result)
            {
                text.AppendLine(result);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IAssistantSessionHost.Session))
        {
            var session = _host.Session;
            if (!ReferenceEquals(_observedSession, session))
            {
                _WatchTranscript(_observedSession, session);
                _observedSession = session;
                OnPropertyChanged(nameof(Session));
                OnPropertyChanged(nameof(HasSession));
                OnPropertyChanged(nameof(HasMessages));
            }
        }

        if (e.PropertyName is null or nameof(IAssistantSessionHost.Activity))
        {
            OnPropertyChanged(nameof(IsUnavailable));
        }

        if (e.PropertyName is null or nameof(IAssistantSessionHost.UnavailableReason))
        {
            OnPropertyChanged(nameof(UnavailableReason));
        }
    }

    // Moves the transcript watch from one session to the next, so `HasMessages` is re-raised as rows arrive rather than
    // only when the session itself is swapped (AC-935).
    private void _WatchTranscript(SessionViewModel? previous, SessionViewModel? next)
    {
        if (previous is not null)
        {
            previous.Transcript.CollectionChanged -= _OnTranscriptChanged;
            previous.PendingAttachments.CollectionChanged -= _OnPendingAttachmentsChanged;
        }

        if (next is not null)
        {
            next.Transcript.CollectionChanged += _OnTranscriptChanged;
            next.PendingAttachments.CollectionChanged += _OnPendingAttachmentsChanged;
        }
    }

    // Only HasMessages: the rows themselves are bound straight to the collection and need no help from here.
    private void _OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasMessages));

    // CanSend's other dependency (see `_inputText`'s own fix note) — an attachment added or removed on the
    // composer must re-enable/disable Send the same way typed text does.
    private void _OnPendingAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(CanSend));

    // Deliberately does not touch `Session` in any way: this runs when the window closes, and closing this window must
    // never end the assistant's conversation (criterion 7).
    public void Dispose()
    {
        _host.PropertyChanged -= _OnHostPropertyChanged;
        _WatchTranscript(_observedSession, next: null);

        // AC-776/AC-774: the one subscription this view model holds on something other than its own host — must
        // come off on close, or every reopened chat window leaves another handler chained to the live session list.
        if (_cockpit is not null)
        {
            _cockpit.Sessions.CollectionChanged -= _OnCockpitSessionsChanged;
        }
    }
}

// Wrapping it here means that fallback logic lives in one place, in code that can be unit-tested directly, rather than
// as three separate converters or a MultiBinding the XAML would otherwise need (AC-545).
public sealed record AssistantSpawnLogRowViewModel(
    string When,
    string What,
    string Who,
    string Where,
    string Session,
    string StartDetails,
    string? Refusal)
{
    // Whether this row carries a refusal reason — the row template shows the italic line only then.
    public bool HasRefusal => Refusal is { Length: > 0 };

    // Whether there is a session to name.
    public bool HasSession => Session.Length > 0;

    // Whether the profile-and-folder line applies. Only a start has them: a stop names a session that is already
    // running under a profile chosen long ago, and printing "(profile default)" under it would claim a folder for
    // an action that started nothing.
    public bool HasStartDetails => StartDetails.Length > 0;

    public static AssistantSpawnLogRowViewModel From(AssistantSpawnAuditEntry entry) => new(
        entry.At.ToLocalTime().ToString("dd MMM HH:mm"),
        _DescribeWhat(entry),
        // Criterion 5 names the caller first, and it is the whole reason SpawnCaller exists: once AC-436 lands, a
        // coordinator's spawn and the assistant's are the same shape of entry and only this word tells them apart
        // (AC-795).
        entry.Caller switch
        {
            SpawnCaller.Assistant => "assistant",
            SpawnCaller.Controller => "paired controller",
            _ => $"coordinator ({entry.CallerPaneId ?? "unknown pane"})",
        },
        entry.WorkspaceName ?? (entry.WorkspaceId.Length > 0 ? entry.WorkspaceId : "—"),
        entry.SessionName ?? entry.PaneId ?? string.Empty,
        entry.Action == AssistantSpawnAction.Start
            ? $"{entry.Profile ?? "—"}  ·  {entry.WorkingDirectory ?? "(profile default)"}"
            : string.Empty,
        entry.Refusal);

    // A gate that only logs what it let through cannot show it working (IAssistantSpawnAuditLog's own remarks) —
    // so a refusal gets its own label rather than reading identically to a spawn that actually happened.
    private static string _DescribeWhat(AssistantSpawnAuditEntry entry) => (entry.Action, entry.Refusal) switch
    {
        (AssistantSpawnAction.Start, null) => "Started",
        (AssistantSpawnAction.Start, _) => "Start refused",
        (AssistantSpawnAction.Stop, null) => "Stopped",
        (AssistantSpawnAction.Stop, _) => "Stop refused",
        // An action this window predates — a trail written by a newer build, or a value added to the enum without
        // this switch being revisited. It still has a row and still reads honestly, rather than throwing a
        // MatchFailureException in a flyout the operator opened to check what had been started.
        var (action, refusal) => refusal is null ? action.ToString() : $"{action} refused",
    };
}
