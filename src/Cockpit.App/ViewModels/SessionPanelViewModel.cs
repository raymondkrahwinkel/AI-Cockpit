using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.UsagePill;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions;
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
    /// </summary>
    public string PaneId { get; } = Guid.NewGuid().ToString("n");

    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Session 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Session";

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
            Title = trimmed;
        }

        IsRenaming = false;
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
                ContextThreshold = declared.DefaultThresholdPercent;
            }
            else
            {
                windows.Add(new SessionRateWindow(declared.Label, reading.UsedPercent, reading.ResetsAt, declared.DefaultThresholdPercent));
            }

            _thresholds[declared.Label] = declared.DefaultThresholdPercent;
            described.Add(_DescribeReading(declared, reading));
            _RaiseOrClearWarning(declared, reading);
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

    // Which signals are currently over their threshold, so the bar is raised on the crossing rather than on every
    // poll. A figure that drops back is forgotten, and crossing again says so again — the reset is real, because a
    // compaction genuinely empties the window and the next fill is news.
    private readonly HashSet<string> _announced = [];

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

    /// <summary>
    /// The provider's own conversation id, when this session has one — what a resume aims at if the pane has since
    /// been closed. Null for a session kind or provider that reports none.
    /// </summary>
    public virtual string? ConversationId => null;

    /// <summary>Dismisses the current warning; the same signal stays quiet until it drops back and crosses again.</summary>
    [RelayCommand]
    private void DismissUsageWarning() => UsageWarning = string.Empty;

    private void _RaiseOrClearWarning(PluginUsageSignal signal, PluginUsageReading reading)
    {
        if (reading.UsedPercent < signal.DefaultThresholdPercent)
        {
            // Back under: forget it, so the next crossing is announced rather than swallowed as already-said.
            _announced.Remove(signal.Key);
            return;
        }

        if (!_announced.Add(signal.Key))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(signal.Description) ? signal.Label : signal.Description;
        var used = Math.Round(reading.UsedPercent, MidpointRounding.AwayFromZero);
        var returns = reading.ResetsAt is { } at ? $", back {at.ToLocalTime():ddd HH:mm}" : string.Empty;

        UsageWarning = $"{name} is {used:0}% used{returns}.";
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
    private IReadOnlyList<UsagePillField> _usagePillVisibleFields = [UsagePillField.Context];

    /// <summary>
    /// The mini-pills the header renders (AC-105): one per selected field the session actually has data for, in
    /// the operator's chosen order. Rebuilt whenever the selection or any underlying metric changes.
    /// </summary>
    public ObservableCollection<UsagePillItem> UsagePillItems { get; } = [];

    /// <summary>
    /// Whether the standalone token/cost meter shows (#8): only when there is usage, the operator has not put
    /// session usage on the pill itself (AC-105) — so the same figure never appears twice on the header — and the
    /// reading level is not suppressing it (AC-138: Focus/Simple prefer the usage pill over the "$" cost figure).
    /// </summary>
    public bool ShowTokenMeter => HasUsage && !UsagePillVisibleFields.Contains(UsagePillField.SessionUsage) && !SuppressCostMeter;

    /// <summary>
    /// Whether a reading level is hiding the standalone token/cost meter (AC-138): false on the base (TTY and the
    /// developer default show it), overridden by the SDK session to hide the "$" figure at Focus and Simple, where
    /// the subscription-friendly usage pill (ctx / rate windows) carries usage instead.
    /// </summary>
    protected virtual bool SuppressCostMeter => false;

    /// <summary>
    /// Whether the header's kind chip (TTY / SDK / provider tag) shows: by default whenever there is a label. The SDK
    /// session overrides this to drop the chip at the Simple reading level (AC-138), where a model/provider tag is
    /// jargon the level exists to hide.
    /// </summary>
    public virtual bool ShowKindChip => !string.IsNullOrEmpty(KindLabel);

    partial void OnKindLabelChanged(string? value) => OnPropertyChanged(nameof(ShowKindChip));

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
        OnPropertyChanged(nameof(ShowTokenMeter));
    }

    partial void OnHasUsageChanged(bool value)
    {
        RebuildUsagePillItems();
        OnPropertyChanged(nameof(ShowTokenMeter));
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
    private void RebuildUsagePillItems()
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

        OnPropertyChanged(nameof(HasUsagePillRegion));
        OnPropertyChanged(nameof(ShowChevronDivider));
    }

    private UsagePillItem? BuildUsagePillItem(UsagePillField field) => field switch
    {
        UsagePillField.Context when ContextUsedPercent is { } percent =>
            new UsagePillItem($"ctx {percent:0}%", UsageSeverity.BrushKeyFor(percent, _ThresholdFor("ctx")), $"Context window: {percent:0}% used"),
        UsagePillField.SessionUsage when HasUsage =>
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
    private ITranscriptCleanupService? _cleanupService;
    private IOpenMicState? _openMicState;

    /// <summary>
    /// Whether open-mic dictation is listening right now — read live, since the operator toggles it at runtime.
    /// The push-to-talk key gate uses it to stand the local hotkey down while open-mic is on (see
    /// <c>PushToTalkKeyGate</c>), so a held key does not transcribe the same speech the open mic already is.
    /// </summary>
    public bool OpenMicActive => _openMicState?.IsListening ?? false;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.ReadAloudMode"/>: how a reply is rendered before read-aloud synthesis (verbatim / naturalized / summarized) (#35).</summary>
    [ObservableProperty]
    private ReadAloudMode _readAloudMode = ReadAloudMode.Verbatim;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TurnAckMode"/>: how a turn-start acknowledgement is produced (off / preset phrase / local LLM) (AC-99).</summary>
    [ObservableProperty]
    private TurnAckMode _turnAckMode = TurnAckMode.InstantPhrases;

    // Rotates the preset acknowledgement phrases so back-to-back turns do not repeat the same one.
    private int _turnAckPhraseIndex;

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

    /// <summary>Transient status text ("Listening...", "Transcribing...") the view can surface next to the input while a hold is in progress.</summary>
    [ObservableProperty]
    private string _voiceStatus = string.Empty;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoSubmitAfterVoice"/>: when true a finished transcript is submitted right after injection (see <see cref="OnVoiceSubmitRequested"/>) instead of waiting for a manual send.</summary>
    [ObservableProperty]
    private bool _autoSubmitAfterVoice;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TtsVoiceSid"/> — the SupertonicTTS speaker used for read-aloud (#35). Loaded on the shared base even though only the SDK session kind triggers synthesis, the same "load every voice field once" approach as the other voice settings here.</summary>
    [ObservableProperty]
    private int _ttsVoiceSid = 1;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.ReadAloudLanguage"/> — the preferred base language ("en"/"nl") for read-aloud (#35): unmarked text speaks in it and the naturalize/summarize pass leans to it.</summary>
    [ObservableProperty]
    private string _readAloudLanguage = "en";

    /// <summary>
    /// Per-session read-aloud toggle (#35/#35b): when true, completed assistant replies are extracted
    /// and enqueued for TTS playback. Shared on the base since both session kinds offer the toggle, even
    /// though the source differs — the SDK session reads its already-open event stream at turn
    /// completion, the TTY session tails the live JSONL transcript (see
    /// <see cref="OnReadAloudToggleChanged"/>). Ephemeral runtime state, off by default.
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

        OnReadAloudToggleChanged(value);
    }

    /// <summary>
    /// Hook for a session kind whose read-aloud source needs starting/stopping when the toggle flips.
    /// No-op by default (the SDK session needs no separate start/stop — it just checks the flag at each
    /// turn completion); the TTY session overrides this to begin/end tailing the transcript.
    /// </summary>
    protected virtual void OnReadAloudToggleChanged(bool isEnabled)
    {
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
        ITranscriptCleanupService? cleanupService = null,
        IOpenMicState? openMicState = null)
    {
        _voicePushToTalk = voicePushToTalk;
        _voiceSettingsStore = voiceSettingsStore;
        _voicePlaybackQueue = voicePlaybackQueue;
        _cleanupService = cleanupService;
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
        ReadAloudMode = settings.ReadAloudMode;
        TurnAckMode = settings.TurnAckMode;
    }

    /// <summary>
    /// Extracts the prose from assistant text and enqueues it for read-aloud (#35), first rewriting it into
    /// natural spoken sentences via the local LLM when <see cref="ReadAloudMode"/> is Naturalized or Summarized
    /// (falling back to the plain extracted prose if the LLM is unavailable). The extractor already strips
    /// code/tables and swaps paths/URLs for spoken words; the LLM pass smooths the rest and tags language runs
    /// (<c>[[nl]]</c>/<c>[[en]]</c>) so mixed Dutch/English replies speak each segment in its own language. A no-op
    /// when the playback queue was never wired (design-time/tests) or there is nothing to say. Shares the one
    /// rendering path with the Options "Test" button via <see cref="ReadAloudPipeline"/>.
    /// </summary>
    protected Task EnqueueReadAloudAsync(string text) =>
        _voicePlaybackQueue is null
            ? Task.CompletedTask
            : ReadAloudPipeline.SpeakAsync(_voicePlaybackQueue, _cleanupService, text, ReadAloudMode, TtsVoiceSid, ReadAloudLanguage);

    /// <summary>
    /// Speaks a short acknowledgement as a turn starts (AC-99) so a voice conversation is not met with silence while
    /// the agent works. Only when read-aloud is on (the operator is already listening to the cockpit) and a queue is
    /// wired; the mode picks a rotating preset phrase or a local-LLM line (which falls back to a preset). Shares the
    /// barge-in-aware playback queue, so a push-to-talk hold cuts it off like any other read-aloud. Fire-and-forget,
    /// the same as <see cref="EnqueueReadAloudAsync"/> — the acknowledgement is a nicety, never load-bearing.
    /// </summary>
    protected async Task SpeakTurnAcknowledgmentAsync(string userMessage)
    {
        if (_voicePlaybackQueue is null || !ReadResponsesAloud || TurnAckMode == TurnAckMode.Off)
        {
            return;
        }

        _turnAckPhraseIndex = await TurnAcknowledgmentPipeline.SpeakAsync(
            _voicePlaybackQueue, _cleanupService, TurnAckMode, _turnAckPhraseIndex, userMessage, TtsVoiceSid, ReadAloudLanguage);
    }

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
    public async Task EndVoiceHoldAsync(bool applyCleanup)
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
            var text = await _voicePushToTalk.EndHoldAsync(applyCleanup);
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

        await DisposeCoreAsync();
    }

    /// <summary>Kind-specific teardown (kill the CLI process, stop the transcript tailer), run after read-aloud is silenced.</summary>
    protected abstract ValueTask DisposeCoreAsync();
}
