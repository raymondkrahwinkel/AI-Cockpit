using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.UsagePill;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions;
using Cockpit.App.Services;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface every cockpit session panel shares regardless of mode (SDK chat or TTY terminal):
/// the sidebar/overview title, selection, coarse status, and profile label, plus disposal. Lets
/// <see cref="CockpitViewModel"/> manage a mixed collection of <see cref="SessionViewModel"/>
/// (SDK) and <see cref="TtyViewModel"/> (TTY) panels through one type.
/// </summary>
public abstract partial class SessionPanelViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>
    /// Identifies this session pane for as long as it exists — what a plugin uses to say "this one, not the
    /// other three on screen" (exposed as <c>IPluginSessionContext.PaneId</c> / <c>ICockpitSessionObserver.ActivePaneId</c>).
    /// Deliberately not the provider's conversation id (the thing you resume by): panes come and go with the
    /// window, and two panes can even resume the same conversation.
    /// <para>
    /// A fresh guid until <see cref="AdoptPaneId"/> overrides it (AC-410): a pane restored from a saved
    /// <c>WorkspacePane</c> after a restart keeps the id it was persisted under, so the worktree it owned, its
    /// audit-log entries and its scheduled resumes all still find it by the same identity.
    /// </para>
    /// </summary>
    public string PaneId { get; private set; } = Guid.NewGuid().ToString("n");

    // Whether AdoptPaneId has already run — guards the one-time-before-attach contract; a second call is a
    // programming error (a pane being restored twice), not something to silently allow.
    private bool _paneIdAdopted;

    /// <summary>
    /// Overrides <see cref="PaneId"/> with the id a saved pane was persisted under (AC-410), so a restored session
    /// keeps its earlier identity instead of minting a new one. Callable exactly once, and only before the pane is
    /// added to <c>CockpitViewModel.Sessions</c> — nothing has looked this pane up by id yet at that point, so
    /// there is nothing left holding the old one.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called a second time on the same panel.</exception>
    /// <exception cref="ArgumentException"><paramref name="paneId"/> is null or blank.</exception>
    internal void AdoptPaneId(string paneId)
    {
        if (_paneIdAdopted)
        {
            throw new InvalidOperationException($"PaneId was already adopted once (as '{PaneId}'); a pane's identity cannot be reassigned a second time.");
        }

        if (string.IsNullOrWhiteSpace(paneId))
        {
            throw new ArgumentException("A restored pane's id cannot be null or blank.", nameof(paneId));
        }

        PaneId = paneId;
        _paneIdAdopted = true;
    }

    /// <summary>
    /// Whether messages other agents address to this pane reach it on their own, carried by its next outgoing turn
    /// (AC-394), or whether it only ever sees them by calling <c>read_inbox</c> itself. Reported per pane by
    /// <c>list_agents</c>, so a sender can tell which of the two it is talking to.
    /// <para>
    /// False on the base, and overridden by the one kind of pane that can actually do it, rather than the other way
    /// round. A pane kind added later inherits "no passive delivery" — which is the direction that is safe to be
    /// wrong in: a sender told a message will not arrive by itself goes and makes sure it does, while one told it
    /// will, wrongly, does nothing and never finds out. The claim is only worth making by a pane that implements it.
    /// </para>
    /// </summary>
    public virtual bool DeliversInboxAtTurnStart => false;

    /// <summary>
    /// Whether a prompt handed to <see cref="SendPromptAsync"/> right now would actually reach the agent — the
    /// precondition an unprompted turn needs before it is worth composing one (AC-395's wake, AC-234's scheduled
    /// resume).
    /// <para>
    /// Asked separately from <see cref="SendPromptAsync"/>'s own return value because on one pane kind that return
    /// value is not the whole answer: a session whose driver never came up still holds a runtime, and a send into
    /// it completes without going anywhere (see <c>_SendWithWaitingMessagesAsync</c>, which is why mail is only
    /// taken from the inbox once the turn can leave). A wake that reads "true" there would be recorded as having
    /// woken a session that never heard it. Each pane kind answers from the one fact it already holds rather than
    /// from a second check of its own, so the two cannot drift.
    /// </para>
    /// <para>
    /// False on the base for the same reason <see cref="DeliversInboxAtTurnStart"/> is: a pane kind added later
    /// inherits "cannot be handed a turn", and a wake that does not fire is a message that waits, while one that
    /// fires into a pane that cannot take it is a turn the operator paid for and nobody read.
    /// </para>
    /// </summary>
    public virtual bool CanTakeAPrompt => false;

    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Session 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Session";

    /// <summary>
    /// Whether <see cref="Title"/> is still one the cockpit composed itself — "&lt;profile&gt; - 3", the project's
    /// name, "&lt;original&gt; (copy)" — rather than one somebody chose, which is what lets
    /// <c>ICockpitHost.SuggestSessionName</c> label a session after the ticket just linked to it without erasing a
    /// name the operator typed (#AC-310). True until the session is named on purpose, which is any of: typed in the
    /// New-session dialog, an inline rename, an explicit <c>SetSessionName</c>, or a flow naming it through
    /// <c>ICockpitActions.SetActiveSessionStatusAsync</c>. Every one of those four is a decision; the composed ones
    /// are placeholders. Which of the two a starting session got is decided in one place — <c>AddSession</c>, from
    /// <c>NewSessionResult.NameIsComposed</c> — so a new start route cannot forget to say (#AC-324).
    /// </summary>
    public bool HasGeneratedName { get; set; } = true;

    /// <summary>
    /// A short free-text line the agent or a plugin sets to say what this session is doing right now — a ticket it
    /// picked up ("AC-13"), a phase, whatever (#AC-13). Shown under the title in the header and the sidebar; blank
    /// hides it. Distinct from <see cref="SessionStatusLabel"/> (the derived Idle/Busy/Needs-attention state) and
    /// from the provider's own status bar: this one is set from outside — the agent via MCP, or a workflow.
    /// </summary>
    [ObservableProperty]
    private string _statusline = string.Empty;

    /// <summary>
    /// The session's own connection/activity line (e.g. "Connected (12 tools, …)", "Running", "TTY mode") — the
    /// header's activity text when no <see cref="Statusline"/> is set. On the shared base so the one SessionHeaderBar
    /// reads it for every session kind.
    /// </summary>
    [ObservableProperty]
    private string _status = "Not started.";

    /// <summary>
    /// The MCP servers this session actually mounts (#44/AC-130) — the merged session/profile selection, set once
    /// by whichever route launched the pane. <see langword="null"/> when neither named one: an unknown, not
    /// necessarily empty, selection (see AC-537 and <see cref="ConnectedStatusLine"/>).
    /// <para>
    /// On the base, and the single source both the header's count and its hover read from, so the number and the
    /// list cannot come to disagree — the failure this would otherwise have is a count of ten beside a list of
    /// nine, with nothing to say which of the two is right (AC-563 criterion 5).
    /// </para>
    /// <para>
    /// Computed by the launching view model rather than read back from the driver, since nothing on the wire
    /// reports the resolved count after start; re-merging an already-merged value downstream is a no-op
    /// (<c>x ?? y</c> on a non-null <c>x</c>), so holding it here changes nothing about what a session mounts.
    /// </para>
    /// <para>
    /// Names, not resolved registry entries: every real caller's names already exclude the cockpit's own
    /// always-there plumbing, because the New-session checklist only ever offers the servers a operator may pick
    /// (AC-130 profile selections are saved from that same checklist). The one caller that can name an internal
    /// endpoint on purpose — an embedded/Autopilot run naming its own pane-scoped tools — inflates the count by
    /// one in that narrow case; resolving it needs a live, project-scoped catalog read the header does not have,
    /// and a cosmetic count does not justify adding one. Accepted, not silently ignored: pinned by
    /// <c>SessionHeaderStatusAndKindChipTests</c>.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedStatusLine))]
    [NotifyPropertyChangedFor(nameof(McpServersTooltip))]
    private IReadOnlySet<string>? _mcpServerSelection;

    /// <summary>
    /// The header's activity line for the current selection (AC-537). An unknown selection is left unsaid rather
    /// than reported as zero — the count is the one figure here that describes the session's own setup, and a
    /// wrong one is worse than none.
    /// </summary>
    public string ConnectedStatusLine => McpServerSelection is { Count: > 0 } servers
        ? $"Connected ({servers.Count} MCP server{(servers.Count == 1 ? string.Empty : "s")})."
        : "Connected.";

    /// <summary>
    /// What the activity column says on hover: the servers this session mounts, by name (AC-563). It hangs on the
    /// column rather than on the text inside it, so an agent's <c>set_status</c> line cannot carry the list off
    /// with the words it replaces — the list would otherwise be unreachable exactly while a session is working.
    /// <para>
    /// An unknown selection says so. Rendering it as an empty list would read as "this session has no MCP
    /// servers", which is a claim about the world that not being able to work something out does not support
    /// (same rule as AC-550 and AC-544 criterion 6).
    /// </para>
    /// </summary>
    public string McpServersTooltip => McpServerSelection switch
    {
        null => "MCP servers\nNot known for this session — neither it nor its profile named a selection.",
        { Count: 0 } => "MCP servers\nNone — this session was started with the selection empty.",
        var servers => "MCP servers\n" + string.Join('\n', servers.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
    };

    /// <summary>
    /// Mirrors <see cref="Cockpit.Core.Debugging.DebugSettings.ShowDebugControls"/> (#73): whether this
    /// session's header shows the controls that exist to investigate the cockpit (the TTY's Redraw) rather than
    /// to do the work. Seeded by <see cref="CockpitViewModel"/> and kept live from Options.
    /// </summary>
    [ObservableProperty]
    private bool _showDebugControls;

    /// <summary>
    /// The consent request waiting on this session, if any (#AC-47) — set by <see cref="CockpitViewModel"/> when the
    /// broker opens a prompt for this pane, cleared when it resolves. Drives the inline consent banner in the pane
    /// chrome (null hides it). On the shared base so both session kinds (SDK chat, TTY) show it the same way.
    /// </summary>
    [ObservableProperty]
    private ConsentPromptViewModel? _pendingConsent;

    /// <summary>
    /// The process this session runs in, once it has one (#78) — what the resource meter weighs, together with
    /// everything that process spawns. Null for a session that is an HTTP call rather than a process (Ollama,
    /// LM Studio), and null before launch.
    /// </summary>
    [ObservableProperty]
    private int? _processId;

    /// <summary>True while the sidebar row is showing its inline rename text box (context-menu → Rename).</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>The in-progress title while renaming; committed to <see cref="Title"/> or discarded.</summary>
    [ObservableProperty]
    private string _editTitle = string.Empty;

    /// <summary>
    /// The choices this session was created with (profile/kind/mode/model/effort), captured by
    /// <see cref="CockpitViewModel"/> so the context-menu Duplicate can start another just like it.
    /// </summary>
    public NewSessionResult? LaunchResult { get; set; }

    /// <summary>
    /// Whether this pane offers "Clear context" (AC-564). False here, and true for the SDK panel that overrides
    /// it: a TTY session is a real TUI where the operator simply types <c>/clear</c>, so offering a second, less
    /// capable way to do it there would be the confusing one.
    /// </summary>
    public virtual bool SupportsClearContext => false;

    /// <summary>
    /// Whether this pane has a persisted <c>WorkspacePane</c> record in <c>cockpit.json</c> (AC-410) — true for an
    /// AI session (written when it starts, or already there when it is restored), false for a plain terminal pane,
    /// which is out of scope for this feature. Set by <see cref="CockpitViewModel"/>; gates whether closing this
    /// session also removes that record, so a plain terminal's close never writes a no-op workspace change.
    /// </summary>
    internal bool HasPersistedPane { get; set; }

    /// <summary>
    /// The restore plan this pane was brought back with (AC-410), or null for a session that was never restored —
    /// which is what keeps the banner below off every ordinary, freshly started session. Set once by
    /// <see cref="CockpitViewModel.RestoreSessionPanesAsync"/> right after the pane is attached, and cleared the
    /// moment the operator's choice actually starts the session, so the banner disappears exactly when the pane it
    /// describes stops being merely offered and starts running.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestoreOffer))]
    [NotifyPropertyChangedFor(nameof(CanResumeConversation))]
    [NotifyPropertyChangedFor(nameof(RestoreOfferText))]
    [NotifyPropertyChangedFor(nameof(RestoreDegradedReason))]
    private SessionRestorePlan? _restoreOffer;

    /// <summary>Whether the restore-offer banner shows at all.</summary>
    public bool HasRestoreOffer => RestoreOffer is not null;

    /// <summary>Whether "Resume conversation" should be offered — only when the plan is confident the earlier conversation is still there.</summary>
    public bool CanResumeConversation => RestoreOffer?.Availability == SessionRestoreAvailability.Known;

    /// <summary>The banner's headline: what was open and where, before anything has been started again.</summary>
    public string RestoreOfferText
    {
        get
        {
            if (RestoreOffer is not { } offer)
            {
                return string.Empty;
            }

            var where = string.IsNullOrWhiteSpace(offer.Pane.WorkingDirectory)
                ? string.Empty
                : $" in {offer.Pane.WorkingDirectory}";

            return $"This session was open when the cockpit closed{where}. Nothing has started yet.";
        }
    }

    /// <summary>Why the earlier conversation cannot be resumed, for the banner's second line; empty when it can (<see cref="CanResumeConversation"/>).</summary>
    public string RestoreDegradedReason =>
        RestoreOffer is { Availability: not SessionRestoreAvailability.Known } offer ? offer.Explanation : string.Empty;

    /// <summary>
    /// Raised when the operator resolves a restore offer by picking a start (AC-410) — <see cref="CockpitViewModel"/>
    /// starts the session accordingly and clears <see cref="RestoreOffer"/> once it lands. Closing the offer is not
    /// raised here: the banner's Close button goes through <see cref="RaiseCloseRequested"/> directly, the same
    /// self-close path a TTY's "exit" already uses.
    /// </summary>
    public event EventHandler<SessionRestoreChoice>? RestoreDecided;

    /// <summary>"Resume conversation" — picks the earlier conversation back up.</summary>
    [RelayCommand]
    private void ResumeConversation()
    {
        if (RestoreOffer is not null)
        {
            RestoreDecided?.Invoke(this, SessionRestoreChoice.Resume);
        }
    }

    /// <summary>"Start fresh" — starts a new conversation in this pane instead.</summary>
    [RelayCommand]
    private void StartFresh()
    {
        if (RestoreOffer is not null)
        {
            RestoreDecided?.Invoke(this, SessionRestoreChoice.StartFresh);
        }
    }

    /// <summary>
    /// "Close" on the restore-offer banner: the pane was never started, so there is no busy turn to interrupt and
    /// no confirmation to ask for — the same reasoning a TTY's own "exit" close already relies on. Routes through
    /// the ordinary self-close path (<see cref="CloseRequested"/>), which is what makes this "the existing close
    /// path, worktree release included" rather than a bespoke discard.
    /// </summary>
    [RelayCommand]
    private void CloseRestoredPane() => RaiseCloseRequested();

    /// <summary>
    /// Takes a name a plugin proposed — the ticket it just linked to this session (#AC-310) — unless the session
    /// already carries a name somebody chose, in which case it keeps that one and this reports false. The one place
    /// the rule lives, so the pane-id surface (<see cref="CockpitViewModel.SuggestSessionName"/>) and the plugin
    /// host cannot drift apart on what counts as a name worth keeping.
    /// </summary>
    public bool SuggestName(string name)
    {
        if (!HasGeneratedName || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        Title = name.Trim();
        // AC-514: a suggested name only ever lived on this view model — the pane record a restart reads back
        // still carried whatever title the session was created with. Raised so CockpitViewModel can persist it,
        // same as an inline rename. HasGeneratedName is deliberately left as-is (still true): a suggestion is
        // remembered, not "chosen" — a later, better suggestion must still be free to replace it (#AC-324).
        RaiseNameChanged();
        return true;
    }

    /// <summary>Starts an inline rename, seeding the editable title from the current one.</summary>
    public void BeginRename()
    {
        EditTitle = Title;
        IsRenaming = true;
    }

    /// <summary>Commits the inline rename (keeping the current title if the edit is blank).</summary>
    public void CommitRename()
    {
        var trimmed = EditTitle?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            SetNameDirectly(trimmed);
        }

        IsRenaming = false;
    }

    /// <summary>
    /// Sets the title outright — an operator's own word, the same as an inline rename, whether it arrived through
    /// one (<see cref="CommitRename"/>) or through <c>SetSessionName</c>/<c>SetActiveSessionStatusAsync</c>
    /// (#AC-13/#AC-312). <see cref="HasGeneratedName"/> always goes to false: unlike <see cref="SuggestName"/>,
    /// nothing here is a mere proposal a later suggestion may still replace. The one place this combination is
    /// written, so a caller cannot set the title and forget <see cref="RaiseNameChanged"/> (AC-514) — three call
    /// sites once did exactly that, silently, before this existed.
    /// </summary>
    internal void SetNameDirectly(string name)
    {
        Title = name.Trim();
        HasGeneratedName = false;
        // AC-514: without this, the pane record a restart reads back kept whatever title the session was created
        // with — none of Title/HasGeneratedName changing after that ever reached the persisted pane on their own.
        RaiseNameChanged();
    }

    /// <summary>Cancels the inline rename, discarding the edit.</summary>
    public void CancelRename() => IsRenaming = false;

    /// <summary>True while this is <see cref="CockpitViewModel.SelectedSession"/> — drives the sidebar's active-item highlight. Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether this panel's view is shown in the session grid: always in multi-session (grid) mode, and
    /// only when selected in single-pane mode (#24 / Zoom). Set by <see cref="CockpitViewModel"/> whenever
    /// the selection or layout changes, so the one live grid can host every session's view (built once,
    /// keeping its TTY pty) and merely hide the deselected ones instead of a second control rebuilding
    /// them on each switch.
    /// </summary>
    [ObservableProperty]
    private bool _isPaneVisible = true;

    /// <summary>Coarse status for the sidebar/grid overview — see <see cref="ViewModels.SessionStatus"/>.</summary>
    [ObservableProperty]
    private SessionStatus _sessionStatus = SessionStatus.Idle;

    /// <summary>
    /// When this session last did anything — every status change stamps it. The cockpit's idle sweep measures
    /// against this to let a finished session fall back to <see cref="SessionStatus.Idle"/> once it has been
    /// quiet long enough.
    /// </summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Label of the profile the running session was started under, once known.</summary>
    [ObservableProperty]
    private string? _activeProfileLabel;

    /// <summary>
    /// When true, transcript rows show their arrival timestamp (T7). Set by <see cref="CockpitViewModel"/>
    /// from the saved transcript-display setting and updated live when it is toggled in Options. Lives on
    /// the shared base so both session kinds carry it uniformly, though only the SDK chat renders it.
    /// </summary>
    [ObservableProperty]
    private bool _showTimestamps;

    /// <summary>
    /// When true, sending "exit" closes this session once its turn completes (T10). Set by
    /// <see cref="CockpitViewModel"/> from the saved session-behaviour setting and updated live on toggle.
    /// </summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

    /// <summary>
    /// Raised when the session asks to be closed by itself (T10: after an "exit" turn completes), so
    /// <see cref="CockpitViewModel"/> can run its normal close/teardown flow. The panel never closes
    /// itself — the cockpit owns the session collection.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Signals <see cref="CockpitViewModel"/> to close this session through its own flow.</summary>
    protected void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raised whenever <see cref="Title"/> changes after the session already exists — a suggested name
    /// (<see cref="SuggestName"/>) or an inline rename (<see cref="CommitRename"/>) — so <see cref="CockpitViewModel"/>
    /// can persist it to the pane's saved record (AC-514). Not raised for the initial title a session is created
    /// with; that one is written by the same call that first persists the pane.
    /// </summary>
    public event EventHandler? NameChanged;

    private void RaiseNameChanged() => NameChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Test seam: raise <see cref="CloseRequested"/> directly to exercise the cockpit's close wiring.</summary>
    internal void RequestSelfClose() => RaiseCloseRequested();

    /// <summary>
    /// True while a close is awaiting confirmation for this panel, so its sidebar row shows an inline
    /// "Close? / Keep" prompt rather than dropping a busy session on a single click (mirrors the
    /// Manage-profiles remove confirm, L11).
    /// </summary>
    [ObservableProperty]
    private bool _isConfirmingClose;

    /// <summary>
    /// True when closing would interrupt work in flight, so the close asks first — a running turn or a session
    /// whose background sub-agents are still going. Idle/waiting/done sessions close on a single click.
    /// </summary>
    public bool RequiresCloseConfirmation => SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground;

    /// <summary>
    /// True while a backgrounded shell this session started is still running (AC-276). It deliberately does not
    /// affect <see cref="SessionStatus"/> — a dev server or a <c>tail -f</c> never ends, and holding the status on
    /// one would strand the session on "working" forever, which is worse than the premature Done it set out to fix.
    /// It only withholds the "session finished" notification, so a session that is still doing something is not
    /// announced as finished. False for a session kind that cannot observe this.
    /// </summary>
    public virtual bool HasOutstandingBackgroundShells => false;

    /// <summary>Short human-readable label for <see cref="SessionStatus"/>, for the sidebar status row.</summary>
    public string SessionStatusLabel => SessionStatus switch
    {
        SessionStatus.Busy => "Busy",
        SessionStatus.WorkingBackground => "Working (background)",
        SessionStatus.WaitingForInput => "Waiting for input",
        SessionStatus.NeedsAttention => "Needs attention",
        SessionStatus.Done => "Done",
        _ => "Idle",
    };

    /// <summary>What the running session's driver supports (#26), so the view hides controls a local provider does not offer instead of showing dead ones. Defaults to the full Claude-CLI set until a session starts.</summary>
    [ObservableProperty]
    private SessionCapabilities _capabilities = SessionCapabilities.ClaudeCli;

    /// <summary>Short provider label shown next to a non-Claude session ("Ollama"/"LM Studio"); empty for a Claude session, which needs no badge.</summary>
    [ObservableProperty]
    private string _providerBadge = string.Empty;

    /// <summary>
    /// This session's working directory, once known — the SDK session learns it from its <c>init</c> event,
    /// the TTY session from its launch path. Exposed to plugins through the read/observe surface
    /// (<c>ICockpitSessionObserver.ActiveSessionWorkingDirectory</c>) so a directory-scoped contribution can
    /// follow the session in view. Null until known.
    /// </summary>
    [ObservableProperty]
    private string? _workingDirectory;

    /// <summary>
    /// How full the context window is (#45 D7 / AC-37), the header's "ctx" figure. Null until the provider reports
    /// it — a bar reading "0%" would be a claim rather than a silence. On the shared base so the one header control
    /// (SessionHeaderBar) reads it for every session kind.
    /// </summary>
    [ObservableProperty]
    private double? _contextUsedPercent;

    /// <summary>
    /// The provider's usage windows (5h / wk / …), each self-labelled with its used-percent and reset time (AC-37);
    /// empty when the provider reports none. Feeds the shared header's usage pill and its flyout, so both the SDK and
    /// TTY sessions render the same pill from one place.
    /// </summary>
    public ObservableCollection<SessionRateWindow> RateLimits { get; } = [];

    /// <summary>
    /// Whether the header's usage pill shows at all (AC-37): there is a context figure, or at least one usage window.
    /// Gating on ctx alone hid the 5h/wk windows — reachable only through the pill's flyout — whenever a provider
    /// reported rate limits without a ctx figure (e.g. right after a /compact). Depends on both ContextUsedPercent
    /// and the RateLimits collection, so both notify it (the ctx setter and a CollectionChanged subscription).
    /// </summary>
    public bool HasUsagePill => ContextUsedPercent is not null || RateLimits.Count > 0;

    /// <summary>The whole usage story for the pill's hover, including when each window rolls over — the thing a bar cannot say.</summary>
    [ObservableProperty]
    private string _limitsTooltip = string.Empty;

    /// <summary>
    /// Folds a provider's usage readings into the header (AC-229), matching each to the signal that declared it.
    /// On the shared base because it is the one place both session kinds can meet: whatever route reported the
    /// figures, they land here and the same header renders them.
    /// <para>
    /// The host reads nothing into the values beyond the <see cref="PluginUsageSignalKind"/> the provider gave
    /// them — a fill is the context bar, an allowance is a window with a reset. A reading whose key matches no
    /// declaration is dropped rather than guessed at, so a provider that renames a signal loses a bar instead of
    /// gaining a mislabelled one.
    /// </para>
    /// </summary>
    public void ApplyUsage(IReadOnlyList<PluginUsageSignal> signals, IReadOnlyList<PluginUsageReading> readings)
    {
        var described = new List<string>(readings.Count);
        double? context = null;
        var windows = new List<SessionRateWindow>(readings.Count);

        _thresholds.Clear();

        foreach (var reading in readings)
        {
            if (signals.FirstOrDefault(signal => signal.Key == reading.SignalKey) is not { } declared)
            {
                continue;
            }

            if (declared.Kind is PluginUsageSignalKind.Fill)
            {
                context = reading.UsedPercent;
                ContextThreshold = _ResolveThreshold(declared);
            }
            else
            {
                windows.Add(new SessionRateWindow(declared.Label, reading.UsedPercent, reading.ResetsAt, _ResolveThreshold(declared)));
            }

            var threshold = _ResolveThreshold(declared);
            _thresholds[declared.Label] = threshold;
            described.Add(_DescribeReading(declared, reading));
            _RaiseOrClearWarning(declared, reading, threshold);
        }

        ContextUsedPercent = context;

        RateLimits.Clear();
        foreach (var window in windows)
        {
            RateLimits.Add(window);
        }

        LimitsTooltip = string.Join(Environment.NewLine, described);
    }

    // The threshold each rendered figure was measured against, by the label it renders under, so the pill and the
    // bar colour at the point the provider called worth-mentioning rather than at a constant of the host's own.
    private readonly Dictionary<string, double> _thresholds = [];

    /// <summary>Where the context bar starts to colour, as the provider declared it; null before anything has been reported.</summary>
    [ObservableProperty]
    private double? _contextThreshold;

    // Which signals are currently over their threshold and what each of them has to say, oldest crossing first.
    // Membership is what makes the bar rise on the crossing rather than on every poll: a figure that drops back is
    // forgotten, and crossing again says so again — the reset is real, because a compaction genuinely empties the
    // window and the next fill is news. Keeping each sentence rather than only the keys is what lets the bar fall
    // back to a warning that is still true when the one in front of it goes quiet; one string for every signal
    // meant the covered one was lost for good, since its own crossing had already been spent.
    private readonly List<(string Key, string Text)> _standing = [];

    // Which signals the operator has taken down by hand — dismissed, or acted on by scheduling the resume they
    // offered. Separate from _standing because both are true at once: the figure is still over its threshold, and
    // the bar is not to speak of it again until it has been away and come back. Without this, taking one warning
    // down would hand the bar straight to whatever it was covering, which reads as the dismiss not having worked.
    private readonly HashSet<string> _silenced = [];

    // Which signal put the text that is on screen there, and which one the standing offer belongs to. Two keys
    // rather than one because they drift apart: a later crossing overwrites the words while the earlier signal's
    // offer is still standing, and that offer belongs to its own allowance, not to the sentence above it.
    private string? _warnedSignal;
    private string? _offeredSignal;

    /// <summary>
    /// What the session bar says about a signal that has passed the point its provider called worth mentioning
    /// (AC-230), or empty when nothing has. Raised once per crossing: a bar that reappears at 91%, 92%, 93% is
    /// noise, and noise gets ignored exactly when it matters.
    /// </summary>
    [ObservableProperty]
    private string _usageWarning = string.Empty;

    /// <summary>Whether the session bar shows a usage warning at all.</summary>
    public bool HasUsageWarning => UsageWarning.Length > 0;

    partial void OnUsageWarningChanged(string value) => OnPropertyChanged(nameof(HasUsageWarning));

    /// <summary>
    /// Sends a prompt into this session as if it had been typed (AC-234) — how a scheduled resume arrives. Each
    /// session kind knows its own route (the SDK runtime, the terminal's stdin); the base only knows that a session
    /// can be spoken to. Returns false when this session cannot take one right now, so a caller reports a resume
    /// that could not be delivered rather than assuming it landed.
    /// </summary>
    public virtual Task<bool> SendPromptAsync(string prompt) => Task.FromResult(false);

    /// <summary>Dismisses the bar; what it could say stays quiet until each of those figures drops back and crosses again.</summary>
    [RelayCommand]
    private void DismissUsageWarning() => _SilenceTheBar();

    /// <summary>
    /// Where this signal warns for this session (AC-233): what the operator set for the profile, else for the
    /// provider, else what the provider itself declared. One resolver, so the pill, the bar and the warning cannot
    /// end up judging the same figure by different numbers.
    /// </summary>
    private double _ResolveThreshold(PluginUsageSignal signal) =>
        UsageThresholds?.Resolve(UsageProviderId ?? string.Empty, ActiveProfileLabel, signal.Key, signal.DefaultThresholdPercent)
        ?? signal.DefaultThresholdPercent;

    /// <summary>The operator's own thresholds, handed in by the cockpit; null means every signal follows its provider's declaration.</summary>
    public UsageThresholdSettings? UsageThresholds { get; set; }

    /// <summary>Which provider's declarations this session's readings belong to, so a per-provider threshold can be found.</summary>
    public string? UsageProviderId { get; set; }

    private void _RaiseOrClearWarning(PluginUsageSignal signal, PluginUsageReading reading, double threshold)
    {
        if (reading.UsedPercent < threshold)
        {
            // Back under: forget it, so the next crossing is announced rather than swallowed as already-said, and
            // take down what this signal still has on screen. Left standing, a warning outlives its own subject —
            // the context empties on a /clear and the bar goes on saying it is half full until someone clicks a
            // notice about a window that no longer exists away by hand. Being away also lifts the silence, so a
            // signal that comes back is news again rather than staying muted for the life of the session.
            _standing.RemoveAll(standing => standing.Key == signal.Key);
            _silenced.Remove(signal.Key);

            if (_warnedSignal == signal.Key)
            {
                _ShowWhatIsStillWorthSaying();
            }

            if (_offeredSignal == signal.Key)
            {
                _ClearResumeOffer();
            }

            return;
        }

        var name = string.IsNullOrWhiteSpace(signal.Description) ? signal.Label : signal.Description;
        var used = Math.Round(reading.UsedPercent, MidpointRounding.AwayFromZero);
        var returns = reading.ResetsAt is { } at ? $", back {at.ToLocalTime():ddd HH:mm}" : string.Empty;
        var says = $"{name} is {used:0}% used{returns}.";

        var already = _standing.FindIndex(standing => standing.Key == signal.Key);
        if (already >= 0)
        {
            // Still over its line, so its crossing has been spent and the bar does not go back up — a bar that
            // returns at 91%, 92%, 93% is noise. What it would say is kept current all the same: a figure that
            // climbs while another warning covers it must not come back afterwards understating itself.
            _standing[already] = (signal.Key, says);

            if (_warnedSignal == signal.Key)
            {
                UsageWarning = says;
            }
        }
        else
        {
            _standing.Add((signal.Key, says));
            UsageWarning = says;
            _warnedSignal = signal.Key;
        }

        // The offer waits for the allowance to actually be spent, not for the threshold that warns about it
        // (Raymond, 2026-07-24): warning at 90% is "keep an eye on this", and there is nothing to pick up from
        // yet — a session that can still work does not need scheduling. Measured on the figure as shown, so the
        // offer appears exactly when the header reads 100%, whatever the provider reported behind the rounding.
        //
        // Only an allowance can carry it at all: a context window empties on a compaction rather than at a
        // moment, so there is no reset to time a resume to however full it gets.
        // Measured on every reading, not only on the one that crossed the warning threshold: an allowance climbs to
        // spent, it does not usually arrive there. Gated on the first crossing, the offer only ever appeared for a
        // signal whose very first reading past its line already read 100% — so in practice it appeared for nobody.
        // One offer at a time, whichever allowance was spent first: there is one prompt box and one moment on the
        // bar, so a second spent allowance must not take them over. Keyed on there being no offer rather than on
        // this signal not holding it — two allowances at 100% would otherwise hand it back and forth on every
        // poll, rewriting the prompt under whoever is typing into it. When the one holding it rolls over the
        // offer is withdrawn, and the other can take its turn.
        if (_offeredSignal is null
            && signal is { Kind: PluginUsageSignalKind.Allowance, SupportsResume: true }
            && used >= 100
            && reading.ResetsAt is { } moment)
        {
            // A minute past the reset, never on it (Raymond, 2026-07-24): the rollover is the provider's moment,
            // not ours, and a prompt landing on the same second can still meet a spent allowance — clock skew
            // between here and their side is enough. A minute costs nothing and removes the whole question.
            ResumeAt = moment.AddMinutes(1);
            ResumePrompt = signal.DefaultResumePrompt ?? string.Empty;
            ResumeReason = $"{name} is {used:0}% used";
            _offeredSignal = signal.Key;

            // An offer is not the warning that was dismissed. "Keep an eye on this" is what got clicked away; this
            // is the allowance actually being gone, and the buttons that act on it live inside the bar — so being
            // silenced at 91% must not leave the offer sitting behind a hidden banner where nothing can reach it.
            // Dismissing again covers this message too, which is the operator's call to make a second time.
            _silenced.Remove(signal.Key);
            _ShowWhatIsStillWorthSaying();
        }
    }

    // Hands the bar to whichever signal is still over its threshold and has not been taken down, most recent
    // crossing first — the same rule that decides what is shown in the first place, so a bar clearing cannot
    // quietly promote an older warning over a newer one. Nothing left to say means an empty bar, which also takes
    // any resume offer's buttons off screen with it; that is why the offer's own signal being still standing is
    // what keeps it reachable, rather than anything the offer does for itself.
    private void _ShowWhatIsStillWorthSaying()
    {
        for (var i = _standing.Count - 1; i >= 0; i--)
        {
            if (_silenced.Contains(_standing[i].Key))
            {
                continue;
            }

            (_warnedSignal, UsageWarning) = _standing[i];
            return;
        }

        UsageWarning = string.Empty;
        _warnedSignal = null;
    }

    // Takes the bar down as a whole, which is what the operator asked for whether they dismissed it or acted on
    // the resume it offered: everything it could currently say goes quiet until it has been away and come back.
    // Silencing only the sentence on screen would hand the bar straight to the one behind it, which reads as the
    // click not having worked.
    private void _SilenceTheBar()
    {
        foreach (var (key, _) in _standing)
        {
            _silenced.Add(key);
        }

        UsageWarning = string.Empty;
        _warnedSignal = null;
    }

    // Withdraws the offer to pick this session up later. A resume that is already waiting is deliberately left
    // alone: the offer is ours to take back once the allowance it was measured against has rolled over, but a
    // moment the operator committed to is theirs to cancel.
    private void _ClearResumeOffer()
    {
        ResumeAt = null;
        ResumePrompt = string.Empty;
        ResumeReason = string.Empty;
        _offeredSignal = null;
    }

    /// <summary>
    /// Where the prompts waiting on a future moment are kept (AC-231/AC-234). Handed in by the cockpit, which owns
    /// the one scheduler; null in the graphs that schedule nothing, and the offer then never appears.
    /// <para>
    /// Setting it subscribes to the scheduler, which is what makes <see cref="PendingResumeLabel"/> follow reality
    /// instead of being written once and never corrected (AC-368) — including where the session is built after the
    /// scheduler has already loaded, so no event is coming for it.
    /// </para>
    /// </summary>
    public ScheduledResumeCoordinator? Resumes
    {
        get => _resumes;
        set
        {
            if (ReferenceEquals(_resumes, value))
            {
                return;
            }

            if (_resumes is not null)
            {
                _resumes.PendingChanged -= _OnPendingResumesChanged;
            }

            _resumes = value;

            if (_resumes is not null)
            {
                _resumes.PendingChanged += _OnPendingResumesChanged;
            }

            _SyncPendingResumeLabel();
            OnPropertyChanged(nameof(CanOfferResume));
            OnPropertyChanged(nameof(CanChangeResumeMoment));
        }
    }

    private ScheduledResumeCoordinator? _resumes;

    // Straight through, on whichever thread raised it. The coordinator raises on its caller's thread and every
    // caller in the app is the UI thread — see the note on ScheduledResumeCoordinator, which is why that file
    // carries no ConfigureAwait(false).
    private void _OnPendingResumesChanged(object? sender, EventArgs e) => _SyncPendingResumeLabel();

    /// <summary>
    /// Reads the pending line off the scheduler — the one place that decides what it says, so a resume that fired,
    /// lapsed or was cancelled cannot leave its banner behind, and a session handed a scheduler that already knows
    /// about it shows the banner without waiting for an event. A restored pane keeps the id it was saved under
    /// (<see cref="AdoptPaneId"/>, AC-410), so a resume whose moment falls within
    /// <c>ScheduledResumeCoordinator</c>'s restart grace can find this pane again — but only once the operator has
    /// actually started it: <see cref="CanTakeAPrompt"/> is what <c>RunDueAsync</c> checks before sending, so a
    /// pane still only showing its restore offer never receives one silently.
    /// </summary>
    private void _SyncPendingResumeLabel() =>
        PendingResumeLabel = _resumes?.PendingFor(PaneId) is { } waiting
            ? $"Resuming {waiting.DueAt.ToLocalTime():ddd HH:mm}"
            : string.Empty;

    /// <summary>When the allowance behind the current warning rolls over — the moment a resume would be timed to. Null when nothing schedulable is warned about.</summary>
    [ObservableProperty]
    private DateTimeOffset? _resumeAt;

    /// <summary>What a resume would send, starting from the provider's own default and editable before it is scheduled.</summary>
    [ObservableProperty]
    private string _resumePrompt = string.Empty;

    /// <summary>Why the offer is there, in the words the warning used, so the pending line can say what it is waiting for.</summary>
    [ObservableProperty]
    private string _resumeReason = string.Empty;

    /// <summary>Whether the warning carries an offer to pick this session up again when its allowance returns.</summary>
    public bool CanOfferResume => Resumes is not null && ResumeAt is not null && !HasPendingResume;

    partial void OnResumeAtChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(CanOfferResume));
        OnPropertyChanged(nameof(CanChangeResumeMoment));
    }

    /// <summary>
    /// The line shown while a resume is waiting — a silent timer that fires at 07:30 is a surprise, not a feature.
    /// Derived from the scheduler and never set from outside: written by hand it went stale the moment anything
    /// happened to the resume it described (AC-368).
    /// </summary>
    [ObservableProperty]
    private string _pendingResumeLabel = string.Empty;

    /// <summary>Whether a resume is waiting on this session.</summary>
    public bool HasPendingResume => PendingResumeLabel.Length > 0;

    partial void OnPendingResumeLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasPendingResume));
        OnPropertyChanged(nameof(CanOfferResume));
        OnPropertyChanged(nameof(CanChangeResumeMoment));
    }

    /// <summary>Schedules the offered resume: this session, at the allowance's own reset moment, with whatever the prompt field says.</summary>
    [RelayCommand]
    private async Task ScheduleResumeAsync()
    {
        if (Resumes is not { } scheduler || ResumeAt is not { } moment)
        {
            return;
        }

        var prompt = string.IsNullOrWhiteSpace(ResumePrompt) ? "continue" : ResumePrompt.Trim();

        // The pending line follows from the scheduler saying so, not from this command assuming it worked.
        await scheduler.ScheduleAsync(new ScheduledResume(PaneId, moment, prompt, ResumeReason));

        _SilenceTheBar();
    }

    /// <summary>
    /// Asks the operator for a moment and a prompt, starting from whatever this session would have used. Set by
    /// the cockpit, which owns the dialogs; null where there is no way to ask, and the override is then not offered.
    /// </summary>
    public Func<DateTimeOffset, string, Task<(DateTimeOffset Moment, string Prompt)?>>? AskForResumeMoment { get; set; }

    /// <summary>Whether the offered moment can be overridden — the same offer, with the time and prompt yours to change.</summary>
    public bool CanChangeResumeMoment => CanOfferResume && AskForResumeMoment is not null;

    /// <summary>
    /// Schedules the resume at a moment of the operator's choosing instead of the one the allowance dictates
    /// (AC-231). The reset is the sensible default, not a rule — a week that returns at 11:00 on a Saturday is no
    /// use to someone who will not be there until Monday.
    /// </summary>
    [RelayCommand]
    private async Task ChangeResumeMomentAsync()
    {
        if (Resumes is not { } scheduler || AskForResumeMoment is not { } ask || ResumeAt is not { } suggested)
        {
            return;
        }

        var prompt = string.IsNullOrWhiteSpace(ResumePrompt) ? "continue" : ResumePrompt.Trim();
        if (await ask(suggested, prompt) is not { } chosen)
        {
            return;
        }

        await scheduler.ScheduleAsync(new ScheduledResume(PaneId, chosen.Moment, chosen.Prompt, ResumeReason));

        _SilenceTheBar();
    }

    /// <summary>Cancels the resume waiting on this session, dropping it from storage rather than only from view.</summary>
    [RelayCommand]
    private async Task CancelResumeAsync()
    {
        if (Resumes is { } scheduler)
        {
            await scheduler.CancelAsync(PaneId);
        }
    }

    // One hover line per reading: what it is in words, how far along, and when it comes back. Rounded away from
    // zero rather than .NET's banker's rounding, which turns 42.5% into 42% and would quietly under-report on the
    // halves — the wrong direction for a figure you are watching fill up.
    private static string _DescribeReading(PluginUsageSignal signal, PluginUsageReading reading)
    {
        var name = string.IsNullOrWhiteSpace(signal.Description) ? signal.Label : signal.Description;
        var resets = reading.ResetsAt is { } at ? $" — resets {at.ToLocalTime():ddd HH:mm}" : string.Empty;

        return $"{name}: {Math.Round(reading.UsedPercent, MidpointRounding.AwayFromZero):0}% used{resets}";
    }

    /// <summary>
    /// The short "kind" chip on the header (AC-37): "TTY" for a terminal session, the provider tag ("SDK", a plugin
    /// name) for an SDK one. Empty hides the chip. On the base so the one SessionHeaderBar renders it for every kind.
    /// </summary>
    [ObservableProperty]
    private string? _kindLabel;

    /// <summary>
    /// The git branch of the worktree this session was isolated in (AC-85), shown as a header chip when set —
    /// e.g. <c>cockpit/&lt;slug&gt;</c>. Empty/null hides the chip (the session runs in the folder as given). On the
    /// base so the one SessionHeaderBar renders it for every kind that can carry a worktree.
    /// </summary>
    [ObservableProperty]
    private string? _worktreeBranch;

    /// <summary>
    /// AC-439: whether a resource this session has claimed (<c>mcp__cockpit-agents__claim</c>) is also claimed by a
    /// session on a <em>different</em> workspace — a collision AC-393's per-desk partition hides from both agents on
    /// purpose. Recomputed on a UI-thread timer in <see cref="Cockpit.App.Views.CockpitView"/> from
    /// <c>IClaimCollisionMonitor</c>, never from anything an agent's tool result carries: this is operator-only, the
    /// chip <see cref="Controls.SessionHeaderBar"/> shows and nothing else. Not a count or a resource name — every
    /// collision reads the same in phase 1 (see <c>IClaimCollisionMonitor</c> for why).
    /// </summary>
    [ObservableProperty]
    private bool _hasClaimCollision;

    /// <summary>
    /// The project this session works on (AC-163), or null for one belonging to none. On the base for the same
    /// reason as the branch above: every kind of session can start under a project. Carried rather than resolved
    /// on demand because a session outlives the dialog that started it — and a project the operator has since
    /// deleted must not change what a running session was launched with.
    /// <para>
    /// Written at launch and not yet read: what a project decides is resolved into the launch itself (its folder,
    /// its server names, its instructions), so nothing downstream needs to ask which project a running session
    /// belongs to. It is here for the half that does — a session-scoped MCP fan-out that resolves servers as the
    /// project sees them rather than by name out of the unscoped registry.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>
    /// Whether plugin-contributed session-header items show (AC-25/AC-37): true for a real agent session, false for
    /// a plain terminal, where a plugin session indicator has nothing to say. On the base so the one SessionHeaderBar
    /// gates the shared PluginSessionHeaderHost without needing the TTY-only IsTerminal flag.
    /// </summary>
    [ObservableProperty]
    private bool _showPluginHeaderItems = true;

    /// <summary>True once any usage/cost has accrued (#8), so the header's token/cost meter shows only when there is something to show. On the base so the one SessionHeaderBar renders it (a session kind with no usage feed leaves it false).</summary>
    [ObservableProperty]
    private bool _hasUsage;

    /// <summary>Compact token/cost meter text next to the pill, e.g. "45.2k tok · $0.0123" (#8).</summary>
    [ObservableProperty]
    private string _usageSummary = string.Empty;

    /// <summary>Per-bucket usage breakdown for the meter's hover (#8).</summary>
    [ObservableProperty]
    private string _usageTooltip = string.Empty;

    /// <summary>
    /// Which metrics the header's usage pill shows (AC-105), a global preference pushed down from
    /// <see cref="CockpitViewModel"/>. Defaults to just the context window — the original behaviour.
    /// </summary>
    [ObservableProperty]
    // SessionUsage is in the default because the standalone meter it replaced had no opt-in at all: it simply
    // showed whenever usage existed. Leaving it out would have silently dropped the token/cost figure from every
    // header that never visited Options, which is a different change than the one being made here.
    private IReadOnlyList<UsagePillField> _usagePillVisibleFields = [UsagePillField.Context, UsagePillField.SessionUsage];

    /// <summary>
    /// The mini-pills the header renders (AC-105): one per selected field the session actually has data for, in
    /// the operator's chosen order. Rebuilt whenever the selection or any underlying metric changes.
    /// </summary>
    public ObservableCollection<UsagePillItem> UsagePillItems { get; } = [];

    /// <summary>
    /// Whether a reading level vetoes the token/cost figure outright, regardless of the operator's pill selection
    /// (AC-138): false on the base (TTY has no reading level) and on the SDK session except at Simple, whose "no
    /// cost" promise has to hold even when session usage is selected.
    /// </summary>
    protected virtual bool SuppressCostMeter => false;

    /// <summary>
    /// Whether the header's kind chip (TTY / SDK / provider tag) shows: by default whenever there is a label. The SDK
    /// session overrides this to drop the chip at the Simple reading level (AC-138), where a model/provider tag is
    /// jargon the level exists to hide.
    /// </summary>
    public virtual bool ShowKindChip => !string.IsNullOrEmpty(KindLabel);

    partial void OnKindLabelChanged(string? value) => OnPropertyChanged(nameof(ShowKindChip));

    /// <summary>
    /// AC-549: a window the operator ticked in Options that no figure has arrived for. Ticking "5-hour window" on
    /// such a session used to do nothing at all — no segment, no bar, no word — which reads as a broken setting.
    /// The pill itself stays empty (AC-530 criterion 5 — a window whose fill is unknown must not render as 0%);
    /// this is the flyout's line, where the operator looks when they wonder where it went.
    /// <para>
    /// It says "no figure reported", not "not reported by this provider", and the distinction is measured rather
    /// than cautious: captured from a real SDK stream (CLI 2.1.220), <c>rate_limit_event</c> <em>does</em> carry
    /// the five-hour window — <c>{"status":"allowed","resetsAt":…,"rateLimitType":"five_hour"}</c> — but with no
    /// <c>utilization</c> field while the account is not near that limit. The window is reported; its fill is
    /// not. Blaming the provider would have been false, and a terminal session proves it: that route reads
    /// <c>used_percentage</c> straight out of the statusline payload and shows a bar.
    /// </para>
    /// Empty when every ticked window has a figure.
    /// </summary>
    [ObservableProperty]
    private string _unreportedWindowsNotice = string.Empty;

    // Which selected fields name a rate window, by the label that window carries. Session usage and ctx are not
    // windows; anything else a future field adds is not one either until it is listed here.
    private static readonly Dictionary<UsagePillField, string> _WindowFieldLabels = new()
    {
        [UsagePillField.FiveHourWindow] = "5h",
        [UsagePillField.WeeklyWindow] = "wk",
    };

    private string _DescribeUnreportedWindows()
    {
        // Nothing has been reported at all yet (a session that has not had its first usage event): silence is the
        // honest answer there, not a claim about what the provider can do.
        if (!HasUsagePill)
        {
            return string.Empty;
        }

        var missing = UsagePillVisibleFields
            .Where(field => _WindowFieldLabels.ContainsKey(field))
            .Select(field => _WindowFieldLabels[field])
            .Where(label => RateLimits.All(window => window.Label != label))
            .ToList();

        return missing.Count switch
        {
            0 => string.Empty,
            1 => $"{missing[0]}: no figure reported for this session.",
            _ => $"{string.Join(", ", missing)}: no figure reported for this session.",
        };
    }

    /// <summary>Whether the usage pill shows at all: at least one metric segment, or the chevron's detail flyout.</summary>
    public bool HasUsagePillRegion => UsagePillItems.Count > 0 || HasUsagePill;

    /// <summary>Whether a divider sits between the last metric segment and the chevron — only when both are present.</summary>
    public bool ShowChevronDivider => UsagePillItems.Count > 0 && HasUsagePill;

    protected SessionPanelViewModel()
    {
        // HasUsagePill and the mini-pills both depend on the RateLimits collection as well as ContextUsedPercent,
        // so a window being added/cleared has to refresh them too (the ctx setter is covered by the partials below).
        RateLimits.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUsagePill));
            RebuildUsagePillItems();
        };
    }

    partial void OnContextUsedPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(HasUsagePill));
        RebuildUsagePillItems();
    }

    partial void OnUsagePillVisibleFieldsChanged(IReadOnlyList<UsagePillField> value)
    {
        RebuildUsagePillItems();
    }

    partial void OnHasUsageChanged(bool value)
    {
        RebuildUsagePillItems();
    }

    partial void OnUsageSummaryChanged(string value) => RebuildUsagePillItems();

    // The SessionUsage segment shows UsageSummary with UsageTooltip on hover; the usage feed sets the summary
    // before the tooltip, so without rebuilding on the tooltip too the hover text would lag a turn behind.
    partial void OnUsageTooltipChanged(string value) => RebuildUsagePillItems();

    /// <summary>
    /// Rebuilds <see cref="UsagePillItems"/> from the selected fields, keeping only the metrics this session has a
    /// value for — a selected field with no data (a rate window the provider never reported, usage on a session
    /// kind that has none) simply yields no pill, the same silence the single ctx pill kept.
    /// </summary>
    protected void RebuildUsagePillItems()
    {
        UsagePillItems.Clear();
        foreach (var field in UsagePillVisibleFields)
        {
            if (BuildUsagePillItem(field) is { } item)
            {
                // Every segment but the first carries a divider on its left, so they read as one pill.
                UsagePillItems.Add(item with { ShowLeadingDivider = UsagePillItems.Count > 0 });
            }
        }

        UnreportedWindowsNotice = _DescribeUnreportedWindows();

        OnPropertyChanged(nameof(HasUsagePillRegion));
        OnPropertyChanged(nameof(ShowChevronDivider));
    }

    private UsagePillItem? BuildUsagePillItem(UsagePillField field) => field switch
    {
        UsagePillField.Context when ContextUsedPercent is { } percent =>
            new UsagePillItem($"ctx {percent:0}%", UsageSeverity.BrushKeyFor(percent, _ThresholdFor("ctx")), $"Context window: {percent:0}% used"),
        // Gated on SuppressCostMeter as well as on being selected: this segment is now the only place the
        // token/cost figure renders (the standalone meter beside the pill was the same UsageSummary and the same
        // tooltip, so unticking "Session usage" moved the figure instead of removing it — Raymond, live test
        // 2026-07-31). Simple's "no cost" promise therefore has to hold here rather than on the meter.
        UsagePillField.SessionUsage when HasUsage && !SuppressCostMeter =>
            new UsagePillItem(UsageSummary, "CockpitTextSecondaryBrush", UsageTooltip),
        UsagePillField.FiveHourWindow => WindowPillItem("5h"),
        UsagePillField.WeeklyWindow => WindowPillItem("wk"),
        _ => null,
    };

    // The rate windows label themselves ("5h", "wk"); a field maps to the window carrying its label, and yields
    // nothing when the provider reported no such window. Each pill carries only its own figure in the hover — the
    // combined story stays in the chevron's flyout.
    private UsagePillItem? WindowPillItem(string label) =>
        RateLimits.FirstOrDefault(window => window.Label == label) is { } window
            ? new UsagePillItem($"{label} {window.UsedPercent:0}%", UsageSeverity.BrushKeyFor(window.UsedPercent, _ThresholdFor(label)), $"{label}: {window.UsedPercent:0}% used")
            : null;

    // What the provider called worth mentioning for the signal behind this label, or null when the figure came
    // from a route that declares none (an SDK driver reporting windows without signals, or a design-time stub).
    private double? _ThresholdFor(string label) => _thresholds.TryGetValue(label, out var threshold) ? threshold : null;

    /// <summary>
    /// Raised for each chunk of visible text this session produces (assistant text, tool output, or — for the
    /// TTY session — a tailed transcript line), surfaced to plugins via the read/observe surface so a watcher
    /// can scan for an output signal such as a new pull-request url. Fired on the thread the producing code
    /// runs on; the host-side observer marshals to the UI thread before handing it to plugins.
    /// </summary>
    public event EventHandler<string>? OutputTextProduced;

    /// <summary>Surfaces a chunk of produced text to <see cref="OutputTextProduced"/> subscribers (the read/observe surface). No-op for empty text.</summary>
    protected void RaiseOutputText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            OutputTextProduced?.Invoke(this, text);
        }
    }

    /// <summary>
    /// Raised when this session's agent completes a tool call (AC-116), coupling its name and input with the
    /// result — surfaced to plugins via <see cref="ICockpitSessionObserver.ToolActivityObserved"/> so a
    /// contribution can react to a specific tool rather than scan prose. Only the SDK session raises it; the
    /// TTY session does not parse tool calls. Marshalled to the UI thread by the host-side observer.
    /// </summary>
    public event EventHandler<SessionToolActivity>? ToolActivityProduced;

    /// <summary>Surfaces a completed tool call to <see cref="ToolActivityProduced"/> subscribers (the read/observe surface). No-op for a blank tool name (nothing to attribute the result to).</summary>
    protected void RaiseToolActivity(string toolName, string inputJson, string resultContent, bool isError)
    {
        if (!string.IsNullOrEmpty(toolName))
        {
            ToolActivityProduced?.Invoke(this, new SessionToolActivity(PaneId, toolName, inputJson, resultContent, isError));
        }
    }

    private IReadOnlyList<SessionImageAttachment> _currentTurnImages = [];

    /// <summary>
    /// The images the user message that started the current turn carried (AC-116), or empty. Turn-scoped: set
    /// when an image-bearing message is sent (<see cref="SetCurrentTurnImages"/>) and cleared when the turn
    /// completes (<see cref="ClearCurrentTurnImages"/>), so the host-side observer can hand a plugin exactly
    /// this turn's images when it reacts to a tool call, never a stale earlier set.
    /// </summary>
    public IReadOnlyList<SessionImageAttachment> CurrentTurnImages => _currentTurnImages;

    /// <summary>Records the images the just-sent message carried as this turn's images (AC-116).</summary>
    protected void SetCurrentTurnImages(IReadOnlyList<SessionImageAttachment> images) => _currentTurnImages = images;

    /// <summary>Drops the current turn's images (AC-116) — called when the turn completes, so a later image-less turn attaches nothing.</summary>
    protected void ClearCurrentTurnImages() => _currentTurnImages = [];

    private IVoicePushToTalkService? _voicePushToTalk;
    private IVoiceSettingsStore? _voiceSettingsStore;
    private IVoicePlaybackQueue? _voicePlaybackQueue;
    private IOpenMicState? _openMicState;

    /// <summary>
    /// Whether open-mic dictation is listening right now — read live, since the operator toggles it at runtime.
    /// The push-to-talk key gate uses it to stand the local hotkey down while open-mic is on (see
    /// <c>PushToTalkKeyGate</c>), so a held key does not transcribe the same speech the open mic already is.
    /// </summary>
    public bool OpenMicActive => _openMicState?.IsListening ?? false;

    /// <summary>Mirrors the saved voice-input setting, loaded once via <see cref="InitializeVoice"/>. Gates <see cref="BeginVoiceHold"/> so a disabled operator's F9 does nothing.</summary>
    [ObservableProperty]
    private bool _voiceEnabled;

    /// <summary>Avalonia <c>Key</c> enum name for the configured push-to-talk hotkey (e.g. "F9"); the view parses it to compare against <c>KeyEventArgs.Key</c>.</summary>
    [ObservableProperty]
    private string _pushToTalkKeyName = "F9";

    /// <summary>
    /// Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.GlobalPushToTalk"/>. When true, the
    /// <c>VoicePushToTalkCoordinator</c> already routes the OS-wide hotkey to whichever session is
    /// selected, so this session's own local KeyDown/KeyUp handler must no-op — see
    /// <c>PushToTalkKeyGate</c> — to avoid firing the same hold twice.
    /// </summary>
    [ObservableProperty]
    private bool _globalPushToTalkEnabled;

    /// <summary>
    /// The workspace this session belongs to — stamped at creation from whichever workspace was active then.
    /// Two Sessions workspaces are separate desks: each shows only its own sessions, and switching away hides
    /// the rest rather than closing them, so a session keeps running (and keeps its pty) while you look
    /// elsewhere. Empty means "not assigned", which the cockpit reads as belonging to the first workspace —
    /// what a session created before workspaces existed, or in the design-time graph, gets.
    /// </summary>
    [ObservableProperty]
    private string _workspaceId = string.Empty;

    /// <summary>
    /// This session sits on no workspace at all, and no fallback may give it one (AC-543). True only for the
    /// voice assistant — the third session kind, which is neither a pane on a desk nor a headless task with an
    /// owner pane.
    /// </summary>
    /// <remarks>
    /// Distinct from an empty <see cref="WorkspaceId"/>, which means "not assigned" and reads as the first
    /// Sessions workspace. That fallback is right for a session created before workspaces existed and wrong for
    /// this one: it would put the assistant on a roster its neighbours can see, and the mistake would only
    /// surface later, as an agent finding a session nothing accounts for.
    /// <para>
    /// Set once, by <see cref="Services.AssistantSessionHost"/>, at construction. That the host is the only
    /// writer is what makes the assistant's identity established by construction rather than claimed: no agent
    /// can declare that it is the assistant, because nothing it can say sets this.
    /// </para>
    /// <para>
    /// <see cref="Services.SessionWorkspacePlacement"/> is what reads it. Nothing else should ask directly —
    /// the point of that helper is that the rule has one home.
    /// </para>
    /// </remarks>
    public bool BelongsToNoWorkspace { get; internal set; }

    /// <summary>Transient status text ("Listening...", "Transcribing...") the view can surface next to the input while a hold is in progress.</summary>
    [ObservableProperty]
    private string _voiceStatus = string.Empty;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoSubmitAfterVoice"/>: when true a finished transcript is submitted right after injection (see <see cref="OnVoiceSubmitRequested"/>) instead of waiting for a manual send.</summary>
    [ObservableProperty]
    private bool _autoSubmitAfterVoice;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TtsVoiceSid"/> — the SupertonicTTS speaker used for read-aloud (#35). Loaded on the shared base even though only the SDK session kind triggers synthesis, the same "load every voice field once" approach as the other voice settings here.</summary>
    [ObservableProperty]
    private int _ttsVoiceSid = 1;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.ReadAloudLanguage"/> — the language ("en"/"nl") this session's read-aloud is synthesized in (#35), passed on every enqueue.</summary>
    [ObservableProperty]
    private string _readAloudLanguage = "en";

    /// <summary>
    /// Per-session read-aloud toggle (#35): when true, completed assistant replies are extracted and
    /// enqueued for TTS playback as the SDK session's event stream completes a turn. Shared on the base
    /// (the assistant's own session sets it directly, with no header button of its own). Ephemeral
    /// runtime state, off by default.
    /// </summary>
    [ObservableProperty]
    private bool _readResponsesAloud;

    partial void OnReadResponsesAloudChanged(bool value)
    {
        // Turning read-aloud off must silence it now — stop in-flight and queued playback immediately,
        // not just suppress future turns.
        if (!value)
        {
            _voicePlaybackQueue?.StopAll();
        }
    }

    /// <summary>
    /// Wires the shared push-to-talk plumbing and loads the current voice settings. Called from the
    /// concrete view model's constructor rather than folded into the base constructor, since the two
    /// session kinds take a different set of optional services.
    /// </summary>
    protected void InitializeVoice(
        IVoicePushToTalkService? voicePushToTalk,
        IVoiceSettingsStore? voiceSettingsStore,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        IOpenMicState? openMicState = null)
    {
        _voicePushToTalk = voicePushToTalk;
        _voiceSettingsStore = voiceSettingsStore;
        _voicePlaybackQueue = voicePlaybackQueue;
        _openMicState = openMicState;

        if (voiceSettingsStore is not null)
        {
            _ = _LoadVoiceSettingsAsync(voiceSettingsStore);
        }
    }

    private async Task _LoadVoiceSettingsAsync(IVoiceSettingsStore voiceSettingsStore)
    {
        var settings = await voiceSettingsStore.LoadAsync();
        VoiceEnabled = settings.IsEnabled;
        PushToTalkKeyName = settings.PushToTalkKeyName;
        GlobalPushToTalkEnabled = settings.GlobalPushToTalk;
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice;
        TtsVoiceSid = settings.TtsVoiceSid;
        ReadAloudLanguage = settings.ReadAloudLanguage;
    }

    /// <summary>
    /// Extracts the prose from assistant text and enqueues it for read-aloud (#35). The extractor strips
    /// code/tables and swaps paths/URLs for spoken words before anything is queued. A no-op when the playback
    /// queue was never wired (design-time/tests) or there is nothing to say.
    /// </summary>
    protected Task EnqueueReadAloudAsync(string text)
    {
        if (_voicePlaybackQueue is null)
        {
            return Task.CompletedTask;
        }

        var sentences = TtsProseExtractor.Extract(text);
        if (ReadAloudAsOneUtterance && sentences.Count > 1)
        {
            // Joined after extraction, not instead of it: the extractor is what strips code blocks and tables
            // out of something that would otherwise be read character by character, and that job is unrelated
            // to how many clips the result is spoken in.
            sentences = [string.Join(" ", sentences)];
        }

        if (sentences.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Read before the call, not after it: a barge-in that lands while NotifyPreparing is running — a subscriber
        // to its PlaybackActiveChanged event calling StopAll, or the push-to-talk hold doing so from its own thread —
        // bumps the generation in between. Taking the reading afterwards compares a value to itself and lets every
        // such batch through, which is what this guard did before AC-546 removed the awaited rewrite step it used to
        // straddle.
        var generation = _voicePlaybackQueue.Generation;

        // Show the overlay now: the first synthesis (and any first-use model download) runs before a word is
        // heard, and that gap otherwise reads as nothing happening.
        _voicePlaybackQueue.NotifyPreparing();

        if (_voicePlaybackQueue.Generation != generation)
        {
            // Read-aloud was cancelled while this batch was being prepared — drop it instead of speaking over the
            // interrupt the operator just made.
            return Task.CompletedTask;
        }

        _voicePlaybackQueue.Enqueue(sentences, TtsVoiceSid, ReadAloudLanguage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether this session's replies are spoken as one synthesis rather than sentence by sentence. The queue
    /// normally synthesises one sentence ahead while the previous plays, which is right when the reply is long:
    /// you hear the first sentence within a second instead of waiting for the whole thing. It only works while
    /// synthesis keeps up with playback, and measured on this machine it does not — four short sentences took
    /// 14.7 seconds to get through about 8 seconds of speech, so roughly half of it was silence at the sentence
    /// boundaries. False here, because a session's reply can run for paragraphs and one synthesis would be a
    /// long silence before the first word; the assistant sets it, because its answers are short by instruction
    /// and the gaps between sentences are the whole of how it sounds.
    /// </summary>
    public bool ReadAloudAsOneUtterance { get; set; }

    /// <summary>
    /// Starts a push-to-talk hold (KeyDown on the configured hotkey). Returns false — a no-op the
    /// caller should not mark <c>Handled</c> for — when voice is off, unwired, or a hold is already in
    /// progress (the underlying service's own key-repeat guard).
    /// </summary>
    public bool BeginVoiceHold()
    {
        if (!VoiceEnabled || _voicePushToTalk is null)
        {
            return false;
        }

        // A push-to-talk hold means "listen to me now" — interrupt whatever read-aloud playback is
        // running (on this session or any other; the queue is one shared singleton, #35) so it never
        // talks over the dictation.
        _voicePlaybackQueue?.StopAll();

        var started = _voicePushToTalk.BeginHold();
        if (started)
        {
            VoiceStatus = "Listening...";
        }

        return started;
    }

    /// <summary>
    /// Ends the push-to-talk hold (KeyUp), transcribes it, and hands any resulting text to
    /// <see cref="OnVoiceTextReady"/> for this session kind to inject. No-op when voice was never wired.
    /// </summary>
    public async Task EndVoiceHoldAsync()
    {
        if (_voicePushToTalk is null)
        {
            return;
        }

        VoiceStatus = "Transcribing...";

        // First use downloads the model and a GPU runtime before it can transcribe a word, and this line said
        // "Transcribing..." throughout — for minutes. Subscribed only for this hold: the service is shared by
        // every session, so a lasting subscription would narrate one session's download into all of them.
        void OnPreparing(object? _, VoicePreparationProgress step) =>
            Dispatcher.UIThread.Post(() => VoiceStatus = step.Description);
        void OnPrepared(object? _, EventArgs __) =>
            Dispatcher.UIThread.Post(() => VoiceStatus = "Transcribing...");

        _voicePushToTalk.Preparing += OnPreparing;
        _voicePushToTalk.Prepared += OnPrepared;
        try
        {
            var text = await _voicePushToTalk.EndHoldAsync();
            VoiceStatus = string.Empty;
            if (!string.IsNullOrEmpty(text))
            {
                OnVoiceTextReady(text);
                if (AutoSubmitAfterVoice)
                {
                    OnVoiceSubmitRequested();
                }
            }
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Voice error: {ex.Message}";
        }
        finally
        {
            _voicePushToTalk.Preparing -= OnPreparing;
            _voicePushToTalk.Prepared -= OnPrepared;
        }
    }

    /// <summary>
    /// Injects text into this session's input surface (chat input box for SDK, raw pty bytes for TTY) —
    /// the public seam plugins use via <c>ICockpitActions.InjectIntoActiveSessionAsync</c>, reusing the
    /// same per-kind path as a finished voice transcript.
    /// <para>
    /// Places only: whatever the text contains, it does not send. The TTY path reduces it to text a person could have
    /// typed before it reaches the pty, so a line break in an injected issue body cannot act as the Enter the operator
    /// never pressed — that is what separates this from <see cref="InjectAndSubmit"/>.
    /// </para>
    /// </summary>
    public void InjectText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            OnVoiceTextReady(text);
        }
    }

    /// <summary>
    /// Injects text into this session's input surface and submits it — what a self-driving embedded run (AC-152) uses
    /// to hand its agent a work brief without a human turn, unlike <see cref="InjectText"/> which only places the text
    /// for the operator to send. A blank text does nothing.
    /// </summary>
    public void InjectAndSubmit(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        OnVoiceTextReady(text);
        OnVoiceSubmitRequested();
    }

    /// <summary>The brief handed to <see cref="SubmitPromptWhenReady"/> before this session could take one, kept until it can. At most one: a session is spawned with a single opening brief, and a second would be a second turn, not a longer one.</summary>
    private string? _promptHeldUntilReady;

    /// <summary>
    /// True while a brief is waiting for this session to become able to take it — see
    /// <see cref="SubmitPromptWhenReady"/>, whose two <see langword="false"/> results (held, and refused because one
    /// is already held) this is what tells apart. Read by <c>AssistantAgentGateway.SendPromptAsync</c> before it
    /// hands one over, so the assistant is refused out loud instead of being told "held" about a brief it does not
    /// own.
    /// </summary>
    public bool HasPromptWaitingToBeDelivered => _promptHeldUntilReady is not null;

    /// <summary>
    /// Hands this session an opening brief and submits it, waiting for the session to be able to receive one first.
    /// Returns <see langword="true"/> when it went out on the spot and <see langword="false"/> when it is being held
    /// or was refused — never that it was delivered when it was not.
    /// </summary>
    /// <remarks>
    /// What a freshly spawned session needs and <see cref="InjectAndSubmit"/> alone cannot give it. That one is the
    /// operator's-hands seam (a voice transcript, a paste), so it assumes the session is already on screen and able to
    /// hear: on a TTY pane it publishes to the view's pty writer, and a pane whose view has not been realised yet has
    /// no such writer, so the brief goes to nobody and the caller is told nothing. That is the failure the spawn tool
    /// reported <c>ok:true</c> for.
    /// <para>
    /// The condition waited on is <see cref="CanTakeAPrompt"/> — the property that already answers "would a send
    /// actually reach the agent" for AC-234's scheduled resume and AC-395's wake, rather than a new signal or a delay
    /// long enough to work on the machine it was written on. It is strictly stronger than "something is subscribed":
    /// on a TTY pane it is <c>TtyViewModel.PromptSink</c>, which the view wires only once the pty process has actually
    /// spawned (<c>TtyView.StartPty</c>), and on an SDK pane it is a running runtime. Each kind flushes the hold from
    /// the one place its own answer changes, so nothing polls and nothing sleeps.
    /// </para>
    /// <para>
    /// A brief that is held and whose session never comes up is never delivered, and
    /// <see cref="HasPromptWaitingToBeDelivered"/> stays true so a caller can say so rather than claim it landed.
    /// </para>
    /// <para>
    /// <b>The first brief wins; a second while one is still waiting is refused.</b> The field holds one by design
    /// (a session is spawned with a single opening brief, and a second is a second turn rather than a longer one),
    /// and the three ways to enforce that are refuse, queue, or overwrite. Overwrite is the one this method's own
    /// contract forbids: the first caller was told <see langword="false"/> — held, not lost — and a silent
    /// replacement makes that a lie with no refusal, no trace and no signal. A queue was not built because nothing
    /// asks for one: two briefs are a caller mistake (or a retry invited by reading <see langword="false"/> as "try
    /// again"), not a workload. Refusing keeps the promise the first caller was given.
    /// </para>
    /// </remarks>
    public bool SubmitPromptWhenReady(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        if (CanTakeAPrompt)
        {
            InjectAndSubmit(prompt);
            return true;
        }

        // Refused rather than overwritten — see the remarks. Both falses mean "not delivered"; which one it is,
        // HasPromptWaitingToBeDelivered answers, and the caller that cares asks it before calling.
        if (_promptHeldUntilReady is not null)
        {
            return false;
        }

        _promptHeldUntilReady = prompt;
        return false;
    }

    /// <summary>
    /// Sends the brief <see cref="SubmitPromptWhenReady"/> is holding, if there is one and this session can now take
    /// it. Called by each session kind at the single point where its own <see cref="CanTakeAPrompt"/> turns true.
    /// </summary>
    protected void DeliverHeldPrompt()
    {
        if (_promptHeldUntilReady is not { } held || !CanTakeAPrompt)
        {
            return;
        }

        // Cleared before the send, not after: a delivery that throws must not leave the brief queued to be sent a
        // second time by the next readiness change.
        _promptHeldUntilReady = null;
        InjectAndSubmit(held);
    }

    /// <summary>
    /// Injects an open-mic transcript into this session and submits it when <see cref="AutoSubmitAfterVoice"/>
    /// is on — the finished-transcript half of <see cref="EndVoiceHoldAsync"/>, for the hands-free open-mic
    /// path that produces text without a hold.
    /// </summary>
    public void InjectVoiceTranscript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        OnVoiceTextReady(text);
        if (AutoSubmitAfterVoice)
        {
            OnVoiceSubmitRequested();
        }
    }

    /// <summary>Injects a finished voice transcript into this session kind's own input surface (chat input box or raw pty bytes).</summary>
    protected abstract void OnVoiceTextReady(string text);

    /// <summary>
    /// Submits the just-injected transcript when <see cref="AutoSubmitAfterVoice"/> is on — the SDK
    /// session sends its input box, the TTY session writes a trailing carriage return. Default no-op so
    /// a session kind without a submit gesture simply leaves the text in place.
    /// </summary>
    protected virtual void OnVoiceSubmitRequested()
    {
    }

    /// <summary>
    /// Hands a screenshot the operator just took (AC-220) to this session's own input surface, and says what
    /// happened: <see langword="null"/> when it landed, otherwise a short reason to show them.
    /// </summary>
    /// <remarks>
    /// The reason is the point. This is the operator asking for something — they pressed a key, they dragged a
    /// region — so a session kind that cannot carry an image owes them a sentence, not the silence
    /// <see cref="FeedVerifyResultAsync"/> is allowed (that one is an agent's tool call, and the text snapshot
    /// already reached it another way).
    /// </remarks>
    public Task<string?> InjectScreenshotAsync(byte[] screenshotPng)
    {
        if (screenshotPng.Length == 0)
        {
            return Task.FromResult<string?>("The capture came back empty.");
        }

        return ScreenshotRefusalReason is { } refusal
            ? Task.FromResult<string?>(refusal)
            : OnScreenshotCapturedAsync(screenshotPng);
    }

    /// <summary>
    /// Takes a captured screenshot into this session kind's input surface — only called once
    /// <see cref="ScreenshotRefusalReason"/> has said it can. Abstract for the reason
    /// <see cref="OnVoiceTextReady"/> is: a chat session has an input box to hold an attachment, a terminal has a
    /// pty and hands its TUI a path to read.
    /// </summary>
    /// <remarks>
    /// Asynchronous because the terminal route genuinely is: it writes the image to a file first. The chat
    /// session keeps the bytes in hand and simply returns a finished task.
    /// </remarks>
    protected abstract Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng);

    /// <summary>
    /// Why a screenshot cannot go into this session right now, or null when it can (AC-220). One sentence with
    /// two readers: the hotkey path shows it as a toast, the composer's button disables itself and puts it in
    /// its tooltip — so the button and the key can never disagree about what works.
    /// </summary>
    public string? ScreenshotRefusalReason => ScreenshotPlatformRefusal ?? ScreenshotKindRefusal;

    /// <summary>
    /// Set by the cockpit when this platform has no screen capture at all, so a session that could otherwise
    /// take one still says the truth. Null where capture works.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotRefusalReason))]
    [NotifyPropertyChangedFor(nameof(CanCaptureScreenshot))]
    [NotifyPropertyChangedFor(nameof(ScreenshotTooltip))]
    private string? _screenshotPlatformRefusal;

    /// <summary>Why this session <em>kind</em> cannot take one — a terminal cannot, whatever the platform does. Null when it can.</summary>
    protected virtual string? ScreenshotKindRefusal => null;

    /// <summary>
    /// Runs the capture for this session, wired by the cockpit when the session is added. Null in the
    /// design-time and unit-test graphs, where there is no picker to open — and the button disables itself.
    /// </summary>
    public Func<SessionPanelViewModel, Task>? ScreenshotCapture { get; set; }

    /// <summary>Whether the composer's screenshot button does anything: something to run it, and nothing standing in the way.</summary>
    public bool CanCaptureScreenshot => ScreenshotCapture is not null && ScreenshotRefusalReason is null;

    /// <summary>What the button says on hover — the refusal when there is one, so a disabled button explains itself instead of just being grey.</summary>
    public string ScreenshotTooltip => ScreenshotRefusalReason ?? "Take a screenshot into this session";

    /// <summary>Opens the desktop's screenshot picker and attaches the result here. The button's command; the global hotkey takes the same path through the coordinator.</summary>
    [RelayCommand(CanExecute = nameof(CanCaptureScreenshot))]
    private Task CaptureScreenshotAsync() => ScreenshotCapture?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>Re-evaluates the button once the cockpit has handed this panel its capture — a plain setter, so nothing notifies on its own.</summary>
    internal void NotifyScreenshotWiringChanged() => _NotifyScreenshotAvailabilityChanged();

    /// <summary>
    /// The driver settles its capabilities after start, and whether it can see images is one of them — so the
    /// button follows from the capability itself rather than from the one call site that happens to set it. A
    /// second setter added later would otherwise leave a button that stays clickable and silently refuses.
    /// </summary>
    partial void OnCapabilitiesChanged(SessionCapabilities value) => _NotifyScreenshotAvailabilityChanged();

    private void _NotifyScreenshotAvailabilityChanged()
    {
        OnPropertyChanged(nameof(ScreenshotRefusalReason));
        OnPropertyChanged(nameof(CanCaptureScreenshot));
        OnPropertyChanged(nameof(ScreenshotTooltip));
        CaptureScreenshotCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Pushes a visual verify screenshot (AC-86) into this session as a real user turn — the text snapshot rides the
    /// verify tool result instead, so this is only the image a tool result cannot carry. An SDK session on a vision
    /// provider shows it; a TTY session (no image in a pty) and a non-vision provider ignore it. Returns true only
    /// when the screenshot was actually shown. This is the per-kind half of the host verify-feed capability.
    /// </summary>
    public abstract Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng);

    /// <summary>Theme brush resource key for the status dot — resolved in the view via a converter.</summary>
    public string SessionStatusBrushKey => SessionStatus switch
    {
        SessionStatus.Busy => "CockpitStatusBusyBrush",
        SessionStatus.WorkingBackground => "CockpitStatusBackgroundBrush",
        SessionStatus.WaitingForInput or SessionStatus.NeedsAttention => "CockpitStatusWaitingBrush",
        SessionStatus.Done => "CockpitStatusDoneBrush",
        _ => "CockpitTextFaintBrush",
    };

    /// <summary>Keeps the derived status label/brush in sync whenever <see cref="SessionStatus"/> changes, and
    /// records the moment as this session's last activity so the cockpit can tell how long it has been quiet.</summary>
    partial void OnSessionStatusChanged(SessionStatus value)
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(SessionStatusLabel));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
        OnPropertyChanged(nameof(RequiresCloseConfirmation));
    }

    public async ValueTask DisposeAsync()
    {
        // Closing a session that is reading responses aloud must silence it too — otherwise its queued
        // and in-flight utterances keep playing after the panel is gone. The playback queue is one shared
        // singleton (#35), so this is the same blanket stop push-to-talk uses; gating it on this session's
        // own toggle keeps closing a silent session from cutting another that is mid-sentence.
        if (ReadResponsesAloud)
        {
            _voicePlaybackQueue?.StopAll();
        }

        // The scheduler is one singleton for the whole run: a panel that stays subscribed after it closes keeps
        // itself alive for as long as the cockpit is open, one leaked panel per closed session.
        Resumes = null;

        await DisposeCoreAsync();
    }

    /// <summary>Kind-specific teardown (kill the CLI process, stop the transcript tailer), run after read-aloud is silenced.</summary>
    protected abstract ValueTask DisposeCoreAsync();
}
