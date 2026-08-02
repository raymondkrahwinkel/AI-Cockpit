using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewModels;

/// <summary>
/// F-C1 cockpit: a single Claude Code session rendered as a streaming transcript with a
/// chat-style input box and read-only-so-far allow/deny affordances for tool use.
/// </summary>
/// <remarks>
/// Visual layout has not been verified against a running Avalonia window in this sandbox
/// (no display available here) — treat the XAML as unverified until Raymond runs it.
/// </remarks>
public partial class SessionViewModel : SessionPanelViewModel, ITransientService
{
    private readonly ISessionManager? _sessionManager;

    /// <summary>AC-409: written on a live permission-mode switch (see <see cref="OnSelectedPermissionModeChanged"/>). Null in the design-time/unit-test graph, where the switch simply is not persisted.</summary>
    private readonly SessionStateRecorder? _sessionStateRecorder;

    /// <summary>
    /// Resolves a Plugin-provider profile's own display name for the header's kind chip (AC-537) — the same
    /// registry <see cref="Converters.ProfileDisplayConverter"/> uses for the profile picker, injected here rather
    /// than reaching into that converter's static seam. Null in the design-time/unit-test graph, where a plugin
    /// profile's chip falls back to nothing rather than a resolved name (see <see cref="StartWithProfileAsync"/>).
    /// </summary>
    private readonly IPluginProviderRegistry? _pluginProviderRegistry;

    // The session itself — driver, event pump, lifetime — lives in the runtime (#68); this panel is one of its
    // consumers, not its owner. Created once the profile (and therefore the provider) is known, in
    // StartWithProfileAsync. The manager owns it and is the one place it gets stopped.
    private ISessionRuntime? _runtime;

    /// <summary>
    /// The offer this pane was restored with, captured at the top of <see cref="StartConfiguredAsync"/> when it is
    /// still set (AC-410) — the caller (<c>CockpitViewModel._StartRestoredSessionAsync</c>) clears
    /// <see cref="SessionPanelViewModel.RestoreOffer"/> as soon as this method returns, well before the first turn's
    /// result has come back. Consumed, and cleared, by the first <see cref="TurnCompleted"/> this session reports —
    /// a resume that fails does so on that very first turn (<c>error_during_execution</c>, no history to fail on
    /// later), so nothing past it should still read as "this was a resume attempt".
    /// </summary>
    private SessionRestorePlan? _restoredOfferSnapshot;

    /// <summary>The per-session plugin-provider launch options (sandbox, model) from the New-session dialog, set the same way as <see cref="SessionPanelViewModel.McpServerSelection"/> just before <see cref="StartWithProfileAsync"/> reads them.</summary>
    private IReadOnlyDictionary<string, string>? _launchOptions;

    /// <summary>
    /// Tool names this session auto-allows without an operator prompt (AC-215) — an autonomous embedded run's own
    /// control tools (Autopilot's <c>autopilot_step_done</c>, <c>autopilot_validate</c>, …), pre-authorized at embed
    /// time so a self-driving run does not stall mid-run on a permission prompt it has no one to answer. Empty for an
    /// ordinary session, which keeps prompting as before. Only the plugin's own endpoint tools are ever placed here;
    /// file/shell/egress tools are never pre-approved and stay gated by the permission mode and the ConsentBroker.
    /// </summary>
    private IReadOnlySet<string> _preApprovedTools = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether this session auto-allows every tool call without a prompt (AC-215, Raymond 2026-07-23) — the "worktree is
    /// the boundary" stance for an autonomous run isolated in a throwaway worktree, which must run its work tools (Bash,
    /// edits, git) with no one to answer a prompt. False for an ordinary session, which keeps prompting. Set from the
    /// embedded request's <see cref="EmbeddedSessionRequest.PreApproveAllTools"/>.
    /// </summary>
    private bool _preApproveAllTools;

    private TranscriptEntryViewModel? _currentAssistantEntry;

    /// <summary>The reasoning/thinking row currently being streamed into (AC-213), or null when no thinking block is open. Mirrors <see cref="_currentAssistantEntry"/>: contiguous thinking deltas append onto one row rather than spawning a row per delta.</summary>
    private TranscriptEntryViewModel? _currentThinkingEntry;

    /// <summary>The provider block index of <see cref="_currentThinkingEntry"/>; a delta from a different block (e.g. Codex's raw reasoning vs. its summary) starts a fresh row so the two never concatenate.</summary>
    private int _currentThinkingBlockIndex = -1;

    /// <summary>Assistant-text rows added since the last <see cref="TurnCompleted"/> — a turn can produce several (text, tool call, more text), so the read-aloud trigger (#35) reads all of them, not just the last.</summary>
    private readonly List<TranscriptEntryViewModel> _currentTurnAssistantEntries = [];

    /// <summary>
    /// One sub-agent's own streaming state (AC-146) — the same shape as the top-level fields above
    /// (<see cref="_currentAssistantEntry"/>/<see cref="_currentThinkingEntry"/>/<see cref="_currentThinkingBlockIndex"/>),
    /// scoped to one Task tool call's own activity rather than the parent conversation, so a sub-agent's
    /// streamed text/thinking accumulates onto its own rows instead of the top-level ones it runs alongside.
    /// </summary>
    private sealed class SubAgentLane(TranscriptEntryViewModel anchor)
    {
        public TranscriptEntryViewModel Anchor { get; } = anchor;
        public TranscriptEntryViewModel? CurrentAssistantEntry { get; set; }
        public TranscriptEntryViewModel? CurrentThinkingEntry { get; set; }
        public int CurrentThinkingBlockIndex { get; set; } = -1;
    }

    /// <summary>Live sub-agent lanes, keyed by the parent Task tool call's own tool_use_id. Cleared on every <see cref="TurnCompleted"/>: a sub-agent does not outlive the turn that spawned it.</summary>
    private readonly Dictionary<string, SubAgentLane> _subAgentLanes = [];

    /// <summary>One top-level tool call the turn is currently waiting on (AC-532).</summary>
    private readonly record struct ActiveToolCall(string ToolUseId, string Label, DateTimeOffset StartedAt);

    /// <summary>
    /// Top-level tool calls requested but not yet resulted, oldest first (AC-532). Provider-neutral by
    /// construction: driven only by <see cref="ToolUseRequested"/>/<see cref="ToolResult"/>, the two events every
    /// provider that reports tool calls at all raises. Covers exactly the gap "Thinking…" used to leave blank —
    /// a tool call surfacing to its result landing, the longest stretch of a turn with no visible signal. A
    /// sub-agent's own tool calls never reach here (they nest under their Task row via <see cref="_ResolveSubAgentLane"/>,
    /// which is already visible activity); a <see cref="PermissionRequested"/> for one of these does not remove
    /// it either — the call is still outstanding, and the existing pending-permission chip is a separate, stronger
    /// signal alongside it. Only <see cref="TurnCompleted"/>/<see cref="SessionError"/> clear this unconditionally,
    /// so a driver that never sends a matching result (an interrupt, a crash) cannot leave the composer looking
    /// like it is still running a tool that finished with everything else.
    /// </summary>
    private readonly List<ActiveToolCall> _activeToolCalls = [];

    /// <summary>True while a top-level tool call is outstanding — drives the composer's activity band in place of "Thinking…" (AC-532).</summary>
    public bool HasActiveToolActivity => _activeToolCalls.Count > 0;

    /// <summary>
    /// The call the composer's activity band currently reflects (AC-532): the oldest outstanding call still
    /// waiting on a permission decision, if any — that is the one actually stalling the turn, and the whole point
    /// of naming it here — else the most recently requested call, matching the pre-existing "what's the current
    /// step" behaviour for two ordinary tool calls in flight at once.
    /// </summary>
    private ActiveToolCall? _CurrentActiveToolCall()
    {
        if (_activeToolCalls.Count == 0)
        {
            return null;
        }

        foreach (var call in _activeToolCalls)
        {
            if (_IsAwaitingPermission(call.ToolUseId))
            {
                return call;
            }
        }

        return _activeToolCalls[^1];
    }

    /// <summary>
    /// Whether the transcript row for this outstanding tool call is currently paused on a permission prompt
    /// (AC-532) — read straight from <see cref="TranscriptEntryViewModel.IsPendingPermission"/>, the same flag the
    /// pending-permission chip already renders from, rather than a second ledger tracking the same fact.
    /// </summary>
    private bool _IsAwaitingPermission(string toolUseId) =>
        Transcript.LastOrDefault(t => t.ToolUseId == toolUseId)?.IsPendingPermission ?? false;

    /// <summary>The currently-shown activity's label ("Bash  ·  dotnet build"), or empty when none is active.</summary>
    public string ActiveToolActivityLabel => _CurrentActiveToolCall()?.Label ?? string.Empty;

    /// <summary>
    /// "running m:ss" since the currently-shown activity's tool call was requested, recomputed against
    /// <see cref="DateTimeOffset.Now"/> each time it is read — <see cref="RefreshActiveToolActivityAge"/> is what
    /// makes it tick in the view. While that call is paused on a permission prompt this reads "waiting for
    /// permission" instead: the tool is not running, it is blocked on the operator, and a still-climbing number
    /// under a "running" label would misreport a human wait as tool work. No elapsed count is shown for that wait —
    /// once it resolves (allow, deny, or the call otherwise completes) this reverts to the running text or goes
    /// blank, per <see cref="_CurrentActiveToolCall"/>.
    /// </summary>
    public string ActiveToolActivityAgeText
    {
        get
        {
            if (_CurrentActiveToolCall() is not { } call)
            {
                return string.Empty;
            }

            return _IsAwaitingPermission(call.ToolUseId)
                ? "waiting for permission"
                : $"running {_FormatElapsed(DateTimeOffset.Now - call.StartedAt)}";
        }
    }

    /// <summary>Re-raises the age text's change notification (AC-532) — called on a view-owned tick so the composer's elapsed time counts up instead of freezing at whatever it read on first render.</summary>
    public void RefreshActiveToolActivityAge() => OnPropertyChanged(nameof(ActiveToolActivityAgeText));

    /// <summary>"m:ss", matching the approved mockup's notation (e.g. "0:12", "1:05") — the composer band is the first place this ships, so this is the notation a later background-task pop-out (AC-531) follows rather than inventing its own.</summary>
    internal static string _FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    /// <summary>
    /// True while the "Thinking…" band should show (AC-532): a turn is in flight and no tool-activity band is
    /// already covering it. The two bands occupy the same composer slot and are never both visible — the activity
    /// band replaces "Thinking…" for the span it is active, rather than stacking on top (composer height must not
    /// grow).
    /// </summary>
    /// <remarks>
    /// Round 2 moved this off a "first visible output not yet seen" flag onto <see cref="IsBusy"/>, because that
    /// flag was the defect. It was cleared by the assistant's first text and only ever re-armed by a
    /// <see cref="ToolResult"/> — so a model that said something and then went back to work showed nothing at all
    /// until its next tool call. Measured in Raymond's own transcript of 2026-08-01: three such stretches in one
    /// session, 16.6 s, 65.0 s and 82.9 s, every one of them a text block ending and the next
    /// <see cref="ToolUseRequested"/> breaking the silence. The 82.9 s one is the incident he reported.
    /// <para>
    /// A turn being in flight is the honest invariant, and it is the same one that keeps this from hanging:
    /// <see cref="IsBusy"/> is raised on send and dropped on <see cref="TurnCompleted"/>, on
    /// <see cref="SessionError"/> and on a send that never left — the three ways a turn can end, one of which
    /// always happens. Nothing else needs to re-arm anything, which is precisely why the old flag leaked.
    /// </para>
    /// <para>
    /// The trade-off is that "Thinking…" now also stands under streaming text, where it used to vanish at the
    /// first token. That is the deliberate side the ticket asks for — a band that says the session is working
    /// while it visibly writes is redundant; one that says nothing for 83 seconds is wrong.
    /// </para>
    /// </remarks>
    public bool ShowThinkingIndicator => IsBusy && !HasActiveToolActivity;

    /// <summary>Raises every notification the active-tool-activity fields need after <see cref="_activeToolCalls"/> changes.</summary>
    private void _RaiseActiveToolActivityChanged()
    {
        OnPropertyChanged(nameof(HasActiveToolActivity));
        OnPropertyChanged(nameof(ActiveToolActivityLabel));
        OnPropertyChanged(nameof(ActiveToolActivityAgeText));
        OnPropertyChanged(nameof(ShowThinkingIndicator));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowThinkingIndicator));

    /// <summary>
    /// When this pane first saw each currently-outstanding <see cref="BackgroundTask.TaskId"/> in a
    /// <see cref="BackgroundTasksChanged"/> snapshot (AC-531 #8) — the CLI reports no start time, so this stamp is
    /// what each row's <see cref="BackgroundTaskViewModel.AgeText"/> counts up from. A TaskId no longer in the
    /// latest snapshot is removed rather than kept: if the same id is ever reused, it starts a fresh clock instead
    /// of resuming a stale one.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _backgroundTaskFirstSeen = [];

    /// <summary>
    /// Outstanding sub-agents, shells and unrecognised-kind tasks (AC-531), grouped the way the approved mockup
    /// groups them. Built from the same <see cref="_backgroundTasks"/> list <see cref="HasOutstandingBackgroundShells"/>
    /// already reads — the pop-out's own view of the identical, provider-neutral ledger, not a second one.
    /// </summary>
    public ObservableCollection<BackgroundTaskViewModel> BackgroundSubAgents { get; } = [];

    /// <inheritdoc cref="BackgroundSubAgents"/>
    public ObservableCollection<BackgroundTaskViewModel> BackgroundShells { get; } = [];

    /// <summary>A task kind this build does not recognise — carried rather than dropped, same reasoning as the
    /// provider's own wire parser (see <see cref="BackgroundTaskKind.Unknown"/>).</summary>
    public ObservableCollection<BackgroundTaskViewModel> BackgroundOtherTasks { get; } = [];

    public bool HasBackgroundSubAgents => BackgroundSubAgents.Count > 0;

    public bool HasBackgroundShells => BackgroundShells.Count > 0;

    public bool HasBackgroundOtherTasks => BackgroundOtherTasks.Count > 0;

    /// <summary>
    /// True while at least one background task is outstanding. This gates the pop-out's own contents (list vs.
    /// "no background work"); the button itself is always shown, and only its count badge follows this too
    /// (AC-531 #2 — no badge at all at zero, not a "0" badge).
    /// </summary>
    public bool HasBackgroundTasks => _backgroundTasks.Count > 0;

    /// <summary>The button's badge digit — every outstanding task counts, including a kind this build does not
    /// recognise (AC-531 #2).</summary>
    public int BackgroundTaskCount => _backgroundTasks.Count;

    /// <summary>
    /// "2 sub-agents · 1 shell" — the pop-out's own total line, segments joined the same way AC-532's activity
    /// band joins its own. "nothing" when the list is empty (AC-531 #3, the mockup's empty state).
    /// </summary>
    public string BackgroundTaskSummary
    {
        get
        {
            if (_backgroundTasks.Count == 0)
            {
                return "nothing";
            }

            var parts = new List<string>();
            if (BackgroundSubAgents.Count > 0)
            {
                parts.Add(BackgroundSubAgents.Count == 1 ? "1 sub-agent" : $"{BackgroundSubAgents.Count} sub-agents");
            }

            if (BackgroundShells.Count > 0)
            {
                parts.Add(BackgroundShells.Count == 1 ? "1 shell" : $"{BackgroundShells.Count} shells");
            }

            if (BackgroundOtherTasks.Count > 0)
            {
                parts.Add(BackgroundOtherTasks.Count == 1 ? "1 other" : $"{BackgroundOtherTasks.Count} other");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Selects (or, on a second click of the same row, collapses) one background task's detail in the
    /// pop-out (AC-531 #4). Only one row expands at a time, mirroring the mockup.</summary>
    public void ToggleBackgroundTaskSelection(BackgroundTaskViewModel task)
    {
        var makeSelected = !task.IsSelected;
        foreach (var row in BackgroundSubAgents.Concat(BackgroundShells).Concat(BackgroundOtherTasks))
        {
            row.IsSelected = false;
        }

        task.IsSelected = makeSelected;
    }

    /// <summary>
    /// Rebuilds the pop-out's grouped rows from <see cref="_backgroundTasks"/> after every
    /// <see cref="BackgroundTasksChanged"/> (and the wipe on <see cref="SessionError"/>). Reuses row instances by
    /// TaskId rather than recreating them, so a row the operator has expanded stays expanded across an unrelated
    /// task starting or ending elsewhere in the list. Deliberately never called from <see cref="TurnCompleted"/>:
    /// unlike <see cref="_activeToolCalls"/>, background work does not end just because a turn did (AC-531) — a
    /// detached sub-agent or shell keeps running, and this list (and the button's own count) is what still says so
    /// while the composer's tool-activity band and "Thinking…" both go quiet.
    /// </summary>
    private void _RebuildBackgroundTaskRows()
    {
        var now = DateTimeOffset.Now;
        var liveIds = new HashSet<string>();
        foreach (var task in _backgroundTasks)
        {
            liveIds.Add(task.TaskId);
            if (!_backgroundTaskFirstSeen.ContainsKey(task.TaskId))
            {
                _backgroundTaskFirstSeen[task.TaskId] = now;
            }
        }

        // A TaskId no longer reported has finished (or the whole set was wiped, e.g. SessionError): forget its
        // clock so a reused id someday starts fresh rather than resuming a stale one (AC-531 #8).
        foreach (var staleId in _backgroundTaskFirstSeen.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _backgroundTaskFirstSeen.Remove(staleId);
        }

        _SyncBackgroundGroup(BackgroundSubAgents, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.SubAgent));
        _SyncBackgroundGroup(BackgroundShells, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.Shell));
        _SyncBackgroundGroup(BackgroundOtherTasks, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.Unknown));

        OnPropertyChanged(nameof(HasBackgroundSubAgents));
        OnPropertyChanged(nameof(HasBackgroundShells));
        OnPropertyChanged(nameof(HasBackgroundOtherTasks));
        OnPropertyChanged(nameof(HasBackgroundTasks));
        OnPropertyChanged(nameof(BackgroundTaskCount));
        OnPropertyChanged(nameof(BackgroundTaskSummary));
    }

    /// <summary>Adds/removes/updates rows in one kind's group to match <paramref name="tasks"/>, keeping the
    /// existing <see cref="BackgroundTaskViewModel"/> instance for a TaskId that is still present.</summary>
    private void _SyncBackgroundGroup(ObservableCollection<BackgroundTaskViewModel> group, IEnumerable<BackgroundTask> tasks)
    {
        var incoming = tasks.ToList();
        var incomingIds = incoming.Select(task => task.TaskId).ToHashSet();

        for (var i = group.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(group[i].TaskId))
            {
                group.RemoveAt(i);
            }
        }

        foreach (var task in incoming)
        {
            var existing = group.FirstOrDefault(row => row.TaskId == task.TaskId);
            if (existing is null)
            {
                group.Add(new BackgroundTaskViewModel(task.TaskId, task.Kind, task.Description, _backgroundTaskFirstSeen[task.TaskId]));
            }
            else
            {
                existing.UpdateDescription(task.Description);
            }
        }
    }

    /// <summary>Re-raises AgeText's change notification for every row currently listed (AC-531 #8) — called on
    /// the same view-owned tick <see cref="RefreshActiveToolActivityAge"/> uses, so the pop-out's elapsed times
    /// count up instead of freezing at whatever they read on first render. A no-op with nothing outstanding.</summary>
    public void RefreshBackgroundTaskAges()
    {
        foreach (var row in BackgroundSubAgents.Concat(BackgroundShells).Concat(BackgroundOtherTasks))
        {
            row.RaiseAgeChanged();
        }
    }

    /// <summary>
    /// AC-146 defensive fallback: accumulates streaming text for an event that names a parent tool_use_id this
    /// pane never resolved to a lane (the Task tool-use row it names as parent was never seen — a dropped event,
    /// or a stray id; not expected with the current CLI/adapter, which always emits the Task tool-use row before
    /// anything naming it as parent, but not trusted blindly). Kept entirely separate from
    /// <see cref="_currentAssistantEntry"/> so an orphaned chunk can never merge into the genuine top-level reply
    /// it happens to interleave with — still shown, so nothing vanishes silently, but never queued for read-aloud
    /// and never raises the output-text signal, since it cannot be vouched as the session's own answer.
    /// </summary>
    private TranscriptEntryViewModel? _currentOrphanedSubAgentTextEntry;

    /// <summary>
    /// Resolves the sub-agent lane an event with this parent id belongs to (AC-146), lazily creating one the
    /// first time an event names a parent whose anchor tool-use row is already in the top-level transcript. Null
    /// for a top-level event (no parent id) or one naming a parent this pane never saw the tool-use row for — a
    /// caller then treats the latter as an orphaned sub-agent event (still shown, kept out of read-aloud/output
    /// signals — see the call sites), never as a genuine top-level one, so nothing is silently lost <em>and</em>
    /// nothing gets attributed to the operator's own reply that was not it.
    /// </summary>
    private SubAgentLane? _ResolveSubAgentLane(string? parentToolUseId)
    {
        if (string.IsNullOrEmpty(parentToolUseId))
        {
            return null;
        }

        if (_subAgentLanes.TryGetValue(parentToolUseId, out var lane))
        {
            return lane;
        }

        var anchor = Transcript.LastOrDefault(t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == parentToolUseId);
        if (anchor is null)
        {
            return null;
        }

        lane = new SubAgentLane(anchor);
        _subAgentLanes[parentToolUseId] = lane;
        return lane;
    }

    // How many characters of this turn's assistant prose have already been sent to read-aloud (AC-97). A turn
    // pauses on a question/permission and then keeps streaming into the same growing entry afterwards — the Claude
    // driver never re-emits a completed snapshot, so a turn is one appending entry — which is why this tracks a
    // text offset, not an entry count: counting entries would mark the whole (still-growing) entry "spoken" at the
    // first flush and lose everything the reply says after a tool approval. Reset with the list at the turn boundary.
    private int _readAloudFlushedLength;

    // AC-597/598: whether anything has actually been spoken this turn. What decides both fillers — a lead-in is
    // only owed when the model gave none, and a sign of life only when the operator has heard nothing since.
    private bool _spokenSomethingThisTurn;

    // Rotated so the same words never come twice in a row. Deliberately not reset per turn: it is the sequence of
    // fillers the operator hears that has to vary, not the sequence within one turn.
    private int _spokenFillerRotation;

    // AC-598: the clock that says "still on it" through a long wait, and how many times it has said so this turn.
    private DispatcherTimer? _signOfLifeTimer;

    private int _signOfLifeRepeat;

    /// <summary>Set when an "exit" message is dispatched with auto-close on, so the next completed turn closes the session (T10).</summary>
    private bool _closeAfterTurn;

    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = [];

    /// <summary>False until the first transcript row arrives, so the panel can show a calm empty-state hint instead of a void.</summary>
    public bool HasTranscript => Transcript.Count > 0;

    /// <summary>True once the runtime is up and can accept a turn. Gates the empty-state's "type to start" prompt
    /// so it only invites input once the session is actually ready.
    /// <para>
    /// Virtual only so a test can stand in a session that is <em>alive</em>. A running runtime cannot be faked —
    /// it is a real child process — and "alive" is the input to decisions that only exist for a live session:
    /// <c>AssistantSessionHost</c> replaces a dead instance but not a healthy one, and its restart is defined as
    /// the opposite. Without an override, both branches read the same in a test and neither could be told apart.
    /// </para>
    /// </summary>
    public virtual bool IsSessionReady => _runtime is { IsRunning: true };

    /// <summary>The headless route is the one with no <c>/clear</c> of its own, so this is where the action belongs (AC-564).</summary>
    public override bool SupportsClearContext => true;

    /// <summary>True from launch until the runtime settles — up <em>or</em> failed. Drives the "still starting"
    /// banner so it shows only while the session is actively coming up, and never sits stuck reading "starting"
    /// after a launch that failed (where the runtime is assigned but never running).</summary>
    [ObservableProperty]
    private bool _isStarting;

    /// <summary>Images pasted into the input, sent with the next message and cleared afterwards.</summary>
    public ObservableCollection<ImageAttachmentViewModel> PendingAttachments { get; } = [];

    /// <summary>True while at least one image is queued, so the chip strip can hide when empty.</summary>
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    /// <summary>
    /// True when this session's driver actually sends pasted images to the model (#64) — gates
    /// <see cref="AddPastedImage"/> so a provider without <see cref="SessionCapabilities.SupportsVision"/>
    /// (Ollama/LM Studio, the current plugin providers) never silently drops a pasted image. Notified
    /// alongside <see cref="SessionPanelViewModel.Capabilities"/> in <see cref="StartWithProfileAsync"/>,
    /// the one place that property changes after the driver starts.
    /// </summary>
    public bool CanPasteImages => Capabilities is { SupportsVision: true };

    /// <summary>Messages typed while a turn was in flight, dispatched in order as turns complete (T8).</summary>
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages { get; } = [];

    /// <summary>True while the send queue holds a message, so the queued-chip strip can hide when empty.</summary>
    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    /// <summary>
    /// When on, every message queued while a turn was in flight is dispatched together as a single follow-up
    /// turn once the turn completes (AC-145), instead of one-per-turn. Seeded from the operator's
    /// session-behaviour setting at creation and kept live by the cockpit. SDK/chat-session only — TTY has no
    /// local send queue.
    /// </summary>
    [ObservableProperty]
    private bool _combineQueuedMessages;

    /// <summary>
    /// True when there is text or an image to act on, so Send is enabled exactly when it will do
    /// something. It does not gate on <see cref="IsBusy"/>: while a turn runs, Send queues the message
    /// (T8) rather than being disabled, so you can keep typing ahead without losing input.
    /// </summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0;

    /// <summary>
    /// Whether the operator can type into this session's composer (AC-174). An autonomous embedded run (an Autopilot
    /// step agent) starts with this false so the input box reads as off — the run drives itself — and the surface's
    /// "intervene" affordance flips it true to hand the keyboard back. It gates only the <em>view</em> (the input box
    /// is disabled), deliberately not <see cref="CanSend"/>: the host still submits the run's opening brief through the
    /// send path programmatically, which must work even while the composer is off. Defaults to true for every ordinary
    /// session.
    /// </summary>
    [ObservableProperty]
    private bool _isInputEnabled = true;

    /// <summary>
    /// Permission modes offered in the running panel: the three live-switchable modes
    /// (<see cref="SessionOptionCatalog.LivePermissionModes"/>), or — once a session was launched in
    /// bypass — a single locked "Bypass permissions" entry, since the CLI cannot switch a running
    /// session into or out of bypass. The dialog offers the full four via the catalog.
    /// </summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes =>
        IsPermissionModeLocked ? [SelectedPermissionMode] : SessionOptionCatalog.LivePermissionModes;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    /// <summary>
    /// True once the session was launched in bypass: bypass is terminal (launch-only), so the panel
    /// dropdown is disabled rather than offering a switch the CLI would reject — no dead control (#15).
    /// </summary>
    [ObservableProperty]
    private bool _isPermissionModeLocked;

    partial void OnIsPermissionModeLockedChanged(bool value) => OnPropertyChanged(nameof(PermissionModes));

    /// <summary>The Claude model aliases suggested in the editable model field; the field stays free text so a specific model or snapshot can be pinned live, matching the New-session dialog.</summary>
    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    /// <summary>
    /// The running session's model of record: the launch <c>--model</c>, and what a live switch updates. The header
    /// edits it through <see cref="LiveModelText"/> rather than binding here directly, so a switch applies on commit
    /// (Enter/focus-loss) instead of on every keystroke.
    /// </summary>
    [ObservableProperty]
    private ModelOption _selectedModel = SessionOptionCatalog.DefaultModel;

    /// <summary>
    /// The editable text in the header's Claude model field. Setting it has no side effect — the live switch fires
    /// only when <see cref="CommitLiveModel"/> is called (the view commits on Enter, focus-loss, or picking a
    /// suggestion), so typing a snapshot name does not fire a set_model control request per character.
    /// </summary>
    [ObservableProperty]
    private string _liveModelText = SessionOptionCatalog.DefaultModel.Value;

    /// <summary>Thinking-effort levels offered per session; drives the thinking-budget control.</summary>
    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    /// <summary>
    /// The running plugin provider's generic live controls (#45 D4) — Codex's model and effort — populated after
    /// start from the driver's declared options. Empty for Claude and local sessions, which drive their controls
    /// through the typed dropdowns above; a provider with nothing to switch leaves the panel hidden.
    /// </summary>
    public ObservableCollection<LiveControlViewModel> LiveControls { get; } = [];

    /// <summary>True once the running provider declared at least one generic live control, so the panel shows only when it has something in it.</summary>
    public bool HasLiveControls => LiveControls.Count > 0;

    [ObservableProperty]
    private string _inputText = string.Empty;

    // Status now lives on the shared SessionPanelViewModel base (AC-37), read by the one SessionHeaderBar.

    /// <summary>
    /// How many tools this session connected, or why there are none — the line the empty-state card introduces a
    /// fresh session with. The names behind it used to hang off the provider chip as a card; AC-563 removed that,
    /// on AC-537's finding that a total of everything the agent can call (109, in Raymond's case) says nothing
    /// about the operator's own setup. What does is the MCP-server list, which now hangs off the activity column
    /// (<see cref="SessionPanelViewModel.McpServersTooltip"/>).
    /// </summary>
    [ObservableProperty]
    private string _connectedToolsHeading = string.Empty;

    [ObservableProperty]
    private bool _isBusy;


    /// <summary>Shows the "Allow all tools" toggle: a local tool session (has tools, but not Claude's own permission modes) whose every MCP call would otherwise need an Allow click.</summary>
    [ObservableProperty]
    private bool _showToolAutoApprove;

    /// <summary>When on, this session runs tool calls without prompting (still shown as tool rows). Applied live to the driver.</summary>
    [ObservableProperty]
    private bool _autoApproveTools;

    /// <summary>True while a pending permission decision or CLI <c>needs_action</c> signal is outstanding, driving <see cref="SessionStatus.NeedsAttention"/>.</summary>
    private bool _needsAttention;

    /// <summary>True once at least one turn has finished, so an idle session reads as Done rather than Idle — independent of whether a (success) turn added a transcript row (T4).</summary>
    private bool _hasCompletedATurn;

    /// <summary>Running token/cost total for the session (#8), folded from each completed turn's result usage.</summary>
    private readonly SessionUsageMeter _usage = new();

    /// <summary>
    /// When this session's runtime went up, so a persisted snapshot can say how long it had been working (AC-251).
    /// Taken at the launch rather than at construction: an isolated session is built, then waits on a
    /// <c>git worktree add</c> before it starts, and counting that setup as working time would inflate the very
    /// baseline the token-reduction work measures against. Seeded at construction so a session that never launches
    /// still has a sane value.
    /// </summary>
    private DateTimeOffset _startedAt = DateTimeOffset.Now;

    /// <summary>
    /// The most recent write to the usage trail, awaited on teardown (AC-251). The write is not awaited per turn —
    /// a turn settling must not wait on a file — but a session that closes right after its last turn would otherwise
    /// race the process out and lose that turn from the record, which is the one case this ticket is named for.
    /// </summary>
    private Task? _pendingUsageWrite;

    /// <summary>
    /// Where the running totals are kept so they outlive the session (AC-251). Null in the design-time graph and in
    /// tests that build a session without one, which simply keeps the meter in memory as it always was.
    /// </summary>
    private readonly IUsageHistory? _usageHistory;

    /// <summary>
    /// Carries messages other agents left for this pane out with its next turn (AC-394). Optional: a pane built
    /// without it — every design-time and most test constructions — simply sends what it was given, which is the
    /// behaviour every session had before this existed.
    /// </summary>
    private readonly IAgentTurnInboxDelivery? _turnInboxDelivery;

    /// <summary>Whether the operator drives this session or a plugin embedded it (AC-251). Set by the host when it embeds.</summary>
    internal UsageRunKind RunKind { get; set; } = UsageRunKind.Interactive;

    /// <summary>The run this session was embedded for, from <see cref="EmbeddedSessionRequest.RunId"/>; null for a session belonging to no run.</summary>
    internal string? RunId { get; set; }

    /// <summary>The run's human name, from <see cref="EmbeddedSessionRequest.RunLabel"/>.</summary>
    internal string? RunLabel { get; set; }

    // HasUsage, UsageSummary and UsageTooltip now live on the shared SessionPanelViewModel base (AC-37), rendered by
    // the one SessionHeaderBar; _usage still folds each turn's usage into them here.

    // ContextUsedPercent, RateLimits and LimitsTooltip now live on the shared SessionPanelViewModel base (AC-37),
    // so the one SessionHeaderBar control reads the same usage data for every session kind.

    // --- Reading level (AC-138) -------------------------------------------------------------------------------

    /// <summary>The three reading levels offered by this SDK session's header "View" dropdown.</summary>
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    /// <summary>
    /// This SDK session's current reading level (AC-138) — Developer/Focus/Simple. Seeded at start from the
    /// per-session override or the profile's default view, and switchable live from the header "View" dropdown.
    /// Only the SDK session carries one; a TTY session is a raw terminal with no reading level.
    /// </summary>
    [ObservableProperty]
    private ReadingLevel _readingLevel = ReadingLevel.Developer;

    /// <summary>
    /// Only Simple hides the standalone "$" token/cost meter unconditionally (AC-138: "no cost" is that level's
    /// plain-language promise). Focus's own promise — "cost moves to the usage pill" — only holds once the
    /// operator has actually put <see cref="UsagePillField.SessionUsage"/> on the pill (AC-105, a global
    /// preference defaulted to ctx only); Focus used to veto the figure regardless, so a Focus session on default
    /// settings lost the token count with no reachable substitute (AC-536, measured). Since the standalone meter
    /// was retired the figure lives on the pill alone, so this veto now drops that segment — which is what keeps
    /// Simple's promise true even when the operator has session usage selected.
    /// </summary>
    protected override bool SuppressCostMeter => ReadingLevel == ReadingLevel.Simple;

    /// <summary>Simple drops the model/provider kind chip (AC-138) — a tag that is jargon the level exists to hide.</summary>
    public override bool ShowKindChip => ReadingLevel != ReadingLevel.Simple && !string.IsNullOrEmpty(KindLabel);

    partial void OnReadingLevelChanged(ReadingLevel value)
    {
        // The level lives on the session, but each transcript row renders itself from its own copy — push the new
        // level down, re-fold the Focus groups for it, and re-announce the header figures the level shows or hides.
        foreach (var entry in Transcript)
        {
            entry.ReadingLevel = value;
        }

        _RecomputeReadingGroups();
        // Rebuild rather than just re-announce: the token/cost figure is a pill segment now, and Simple drops it
        // (SuppressCostMeter), so switching level changes which segments exist rather than one visibility flag.
        RebuildUsagePillItems();
        OnPropertyChanged(nameof(ShowKindChip));
    }

    // Assigns the session's reading level to each row as it arrives, watches a tool row for a permission decision
    // (which changes whether it folds), and re-forms the fold groups — the one place the transcript's structure is
    // read to group rows, since a single row cannot see its neighbours.
    private void _OnTranscriptChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (TranscriptEntryViewModel entry in e.NewItems)
            {
                entry.ReadingLevel = ReadingLevel;
                entry.PropertyChanged += _OnEntryPermissionChanged;
            }
        }

        _RecomputeReadingGroups();
    }

    private void _OnEntryPermissionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A tool row is added as auto and can turn into a consent row a beat later (the permission request lands after
        // the tool-use event), which pulls it out of any auto-fold run — so re-fold when either flag flips.
        if (e.PropertyName is nameof(TranscriptEntryViewModel.IsPendingPermission) or nameof(TranscriptEntryViewModel.PermissionDecision))
        {
            _RecomputeReadingGroups();
            OnPropertyChanged(nameof(HasPendingPermission));
        }
    }

    /// <summary>
    /// Whether a tool call is waiting on the operator's Allow/Deny <em>right now</em>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SessionStatus.NeedsAttention"/>, which is deliberately stickier: <c>_needsAttention</c>
    /// is set when a prompt appears and cleared only when the operator sends the next message, so a session keeps
    /// flagging itself in the sidebar until someone has actually been back to it. That is right for a list of panes
    /// you are not looking at, and wrong for anything reporting a live state — read as "now", it says a session is
    /// waiting long after it was answered and the turn finished.
    /// <para>
    /// Recomputed from the rows rather than tracked as a second flag: the rows are where a permission is answered
    /// (<c>RespondToPermissionAsync</c> clears <c>IsPendingPermission</c> on the entry), so anything keeping its own
    /// copy would be one more thing to clear on every path that resolves one.
    /// </para>
    /// </remarks>
    public bool HasPendingPermission => Transcript.Any(entry => entry.IsPendingPermission);

    // Re-forms the Focus "N steps run" fold groups (AC-138): a group is a maximal run of two or more consecutive
    // auto tool calls, its first row the anchor that carries the expand toggle and the rest folding under it. Only
    // Focus folds — Developer shows every row, Simple hides auto tools outright — so at the other levels every row is
    // simply un-grouped. Walks the rows once and preserves an anchor's expanded state, so a run growing mid-turn does
    // not snap shut under the operator.
    private void _RecomputeReadingGroups()
    {
        if (ReadingLevel != ReadingLevel.Focus)
        {
            foreach (var entry in Transcript)
            {
                _ClearGroup(entry);
            }

            return;
        }

        var index = 0;
        while (index < Transcript.Count)
        {
            if (!Transcript[index].IsAutoTool)
            {
                _ClearGroup(Transcript[index]);
                index++;
                continue;
            }

            var runStart = index;
            while (index < Transcript.Count && Transcript[index].IsAutoTool)
            {
                index++;
            }

            var runLength = index - runStart;
            if (runLength < 2)
            {
                // A lone auto tool call is not worth a fold line — it stays as its own compact chip.
                _ClearGroup(Transcript[runStart]);
                continue;
            }

            var members = new List<TranscriptEntryViewModel>(runLength);
            for (var i = runStart; i < index; i++)
            {
                members.Add(Transcript[i]);
            }

            var expanded = members[0].IsGroupExpanded;
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                member.IsGroupAnchor = i == 0;
                member.GroupCount = runLength;
                member.IsGroupExpanded = expanded;
                member.GroupToggleRequested = i == 0 ? () => _ToggleGroup(members) : null;
                member.IsInGroup = true;
            }
        }
    }

    private static void _ClearGroup(TranscriptEntryViewModel entry)
    {
        entry.IsInGroup = false;
        entry.IsGroupAnchor = false;
        entry.GroupCount = 0;
        entry.GroupToggleRequested = null;
        // IsGroupExpanded is left as-is: an un-grouped row never reads it, and keeping it makes a later re-fold stable.
    }

    private static void _ToggleGroup(IReadOnlyList<TranscriptEntryViewModel> members)
    {
        var expanded = members.Count > 0 && !members[0].IsGroupExpanded;
        foreach (var member in members)
        {
            member.IsGroupExpanded = expanded;
        }
    }

    // Parameterless constructor kept for the Avalonia previewer design-time context. Seeds a
    // few sample transcript rows so the previewer/Screenshotter render the styled components
    // (thinking, tool-use, collapsed tool-result, pending permission) — does not touch the real
    // DI-backed session.
    public SessionViewModel()
    {
        _eventQueue = new SessionEventQueue(Apply);
        // Sample MCP selection, and the status line derived from it rather than typed out beside it (AC-563):
        // a hard-coded "Connected (3 MCP servers)." next to an unset selection would have every previewer and
        // render showing a count of three over a hover saying the selection is unknown — the exact divergence
        // this ticket's own criterion 5 rules out, staged as if it were normal.
        McpServerSelection = new HashSet<string>(StringComparer.Ordinal) { "youtrack", "depot", "cockpit-local-ci" };
        Status = ConnectedStatusLine;
        ActiveProfileLabel = "raymond@work";
        KindLabel = "SDK";

        // Sample status bars (#45 D7) so the previewer/Screenshotter renders the header's ctx bar and the
        // provider-labelled window bars.
        ContextUsedPercent = 37;
        RateLimits.Add(new SessionRateWindow("5h", 58, null));
        RateLimits.Add(new SessionRateWindow("wk", 82, null));
        LimitsTooltip = "Context window: 37% used";

        // Sample generic live controls (#45 D4) so the previewer/Screenshotter renders the header's live-control panel.
        // Provider-neutral placeholder values on purpose: the core names no provider's models, not even in sample data.
        LiveControls.Add(new LiveControlViewModel(new SessionLiveOption("model", "Model", ["model-large", "model-fast"], "model-large"), (_, _) => Task.CompletedTask));
        LiveControls.Add(new LiveControlViewModel(new SessionLiveOption("effort", "Effort", ["low", "medium", "high"], "medium"), (_, _) => Task.CompletedTask));

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "fix the layout bug in SessionView"));

        // Markdown-rich sample so the previewer/Screenshotter exercise the markdown path (T9):
        // heading, bold, inline code, a fenced code block, and a list.
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            "## Wat er is\n\n" +
            "- `release.yml` builds **only the desktop client** and attaches it to the release.\n" +
            "- There is a `Dockerfile` but **no workflow** pushing the server image.\n\n" +
            "```csharp\nDockPanel.SetDock(topBar, Dock.Top);\n```\n\n" +
            "| Repo | History | Status |\n|------|---------|--------|\n" +
            "| Playground-RK *(private)* | full dev history, `498` commits | your work repo |\n" +
            "| EveTogether *(public)* | squashed base, `3` commits | official repo |\n\n" +
            "More on the [metadata-action](https://github.com/docker/metadata-action) (clickable)."));

        var editTool = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Edit({\"file_path\":\"SessionView.axaml\",\"old_string\":\"...\"})")
        {
            ToolUseId = "sample-tool-1",
            ToolName = "Edit",
            InputJson = "{\"file_path\":\"SessionView.axaml\",\"old_string\":\"...\"}",
            IsExpanded = true,
        };
        editTool.SetResult("{\"success\":true,\"file\":\"SessionView.axaml\",\"changesApplied\":3,\"warnings\":[]}", isError: false);
        Transcript.Add(editTool);

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Bash({\"command\":\"dotnet build\"})")
        {
            ToolUseId = "sample-tool-2",
            ToolName = "Bash",
            InputJson = "{\"command\":\"dotnet build\"}",
            IsPendingPermission = true,
        });


        _TrackPendingAttachments();

        // A sample queued message so the previewer/Screenshotter render the send-queue strip (T8).
        QueuedMessages.Add(new QueuedMessageViewModel(
            "run the tests once the build finishes", [], m => QueuedMessages.Remove(m)));
    }

    public SessionViewModel(
        ISessionManager sessionManager,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null,
        IAgentTurnInboxDelivery? turnInboxDelivery = null,
        SessionStateRecorder? sessionStateRecorder = null,
        IPluginProviderRegistry? pluginProviderRegistry = null)
    {
        _eventQueue = new SessionEventQueue(Apply);
        _sessionManager = sessionManager;
        _usageHistory = usageHistory;
        _turnInboxDelivery = turnInboxDelivery;
        _sessionStateRecorder = sessionStateRecorder;
        _pluginProviderRegistry = pluginProviderRegistry;
        _TrackPendingAttachments();
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState);
    }

    /// <summary>
    /// This is the pane kind turn-start delivery works on (AC-394): the host composes its turns as typed calls on a
    /// runtime, so there is a real moment before one goes out to put a peer's message in — unlike a CLI in a pty,
    /// where the program on the other side decides what a turn is and the host only has bytes.
    /// <para>
    /// Answered from the seam this instance actually holds rather than from its type. The two come apart: a pane
    /// built without one — the design-time graph, and any test that does not ask for it — is a <c>SessionViewModel</c>
    /// that will never carry a message, and a hard-coded <c>true</c> would have it tell the roster otherwise. What a
    /// sender needs to know is whether <em>this</em> pane delivers, not whether panes of its kind can.
    /// </para>
    /// <para>
    /// It is a claim about wiring, not about health. A pane whose session failed to start still answers true: it is
    /// wired for delivery and will deliver once it runs, and nothing waiting for it is lost in the meantime — the
    /// funnel does not take mail for a turn that cannot leave. Whether a pane is answering at all is what
    /// <c>enrolled</c> and its statusline are for, and conflating the two would make this flag flap through every
    /// start-up.
    /// </para>
    /// </summary>
    public override bool DeliversInboxAtTurnStart => _turnInboxDelivery is not null;

    /// <inheritdoc/>
    /// <remarks>
    /// The same condition the cockpit already gates its own unprompted first turn on
    /// (<c>CockpitViewModel._StartEmbeddedSessionAsync</c> checks <see cref="IsSessionReady"/> before injecting an
    /// embedded run's brief), rather than a second reading of the runtime: a driver that never came up leaves a
    /// runtime behind that accepts a send and does nothing with it.
    /// </remarks>
    public override bool CanTakeAPrompt => IsSessionReady;

    private void _TrackPendingAttachments()
    {
        PendingAttachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingAttachments));
            OnPropertyChanged(nameof(CanSend));
        };
        Transcript.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasTranscript));
            _OnTranscriptChanged(e);
        };
        QueuedMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));
        LiveControls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLiveControls));
    }

    /// <summary>Keeps the Send button's enabled state in sync as the input text changes (T8 CanSend).</summary>
    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(CanSend));

    /// <summary>
    /// Starts the session immediately under the profile and options chosen up front in the New-session
    /// dialog (#31) — this replaces the old in-panel Start button and inline profile picker. When
    /// launched in bypass the panel mode dropdown locks, since bypass cannot be switched into or out of
    /// on a running session (#15).
    /// </summary>
    public async Task StartConfiguredAsync(SessionProfile profile, PermissionModeOption mode, ModelOption model, EffortOption effort, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, ReadingLevel? readingLevel = null, IReadOnlyList<string>? preApprovedTools = null, bool preApproveAllTools = false)
    {
        if (_runtime is not null)
        {
            return;
        }

        // AC-410: still set here for a restored pane's first launch — see _restoredOfferSnapshot's own doc for why
        // it has to be captured now rather than read again once the first turn actually completes.
        _restoredOfferSnapshot = RestoreOffer;

        // The reading level (AC-138) opens on the per-session override chosen in the New-session dialog, else the
        // profile's default view, else the app default (Developer). The header dropdown can still switch it live.
        ReadingLevel = readingLevel ?? profile?.Defaults?.DefaultReadingLevel ?? ReadingLevel.Developer;

        // Set the live selectors before starting: the session has no event loop yet, so these do not
        // fire a live control request — they are the launch values StartWithProfileAsync reads. For
        // bypass, lock immediately (right after selecting it) so the dropdown shows the single locked
        // "Bypass permissions" entry without a frame where the selection sits outside the bound list.
        var isBypass = mode.Value == SessionOptionCatalog.BypassPermissionModeValue;
        SelectedPermissionMode = mode;
        IsPermissionModeLocked = isBypass;
        SelectedModel = model;
        LiveModelText = model.Value;
        SelectedEffort = effort;
        // AC-537: fold in the profile's own saved selection here (same merge PluginSessionDriverAdapter.StartAsync
        // applies before resolving the registry), so a caller that passed none — but whose profile has one — is
        // not read back as "nothing" for the header. See McpServerSelection's own doc for why this is safe to
        // do eagerly.
        McpServerSelection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);
        // Pre-authorized tools for a self-driving run (AC-215): auto-allowed in the permission handler below instead
        // of raising a prompt an autonomous run has no one to answer. Empty for an ordinary session.
        _preApprovedTools = preApprovedTools is { Count: > 0 }
            ? new HashSet<string>(preApprovedTools, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _preApproveAllTools = preApproveAllTools;

        // AC-13: hand the provider this session's own pane id, which its plugin turns into COCKPIT_PANE_ID in the
        // child's environment, so the agent can name its own session to the cockpit-session MCP's set_status tool.
        var mergedOptions = launchOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(launchOptions, StringComparer.OrdinalIgnoreCase);
        mergedOptions[WellKnownPluginSessionOptions.PaneId] = PaneId;
        _launchOptions = mergedOptions;

        await StartWithProfileAsync(profile, workingDirectory, resume);

        // StartWithProfileAsync swallows launch failures (it only sets Status); the runtime is left un-started
        // when the CLI never came up. In that case unlock and reset the mode so a failed bypass launch doesn't
        // strand the panel on a phantom, disabled "Bypass permissions" with no session.
        if (_runtime is not { IsRunning: true })
        {
            IsPermissionModeLocked = false;
            SelectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;
        }

        // Refresh the ready-gate (the empty-state's "type to start" prompt) now the launch has settled:
        // true on a live runtime, false when it failed.
        OnPropertyChanged(nameof(IsSessionReady));

        // The one point where this kind's CanTakeAPrompt turns true, so the one point a brief handed over before the
        // runtime existed can go out. Sending it any earlier is what earns the transcript's "The session has not
        // started yet — nothing was sent."; a launch that failed leaves it held rather than sent into nothing.
        DeliverHeldPrompt();
    }

    /// <summary>
    /// AC-564: the SDK route's equivalent of <c>/clear</c>. A headless stream-json session has no slash-command
    /// surface, so a full context could only be escaped by closing the pane and opening another — which also
    /// costs the operator the pane's name and its place in the workspace. This restarts the session in place
    /// instead: the same panel, the same <see cref="SessionPanelViewModel.PaneId"/>, the same profile, working
    /// directory and MCP selection, started through the ordinary path with no <see cref="SessionResume"/>. That
    /// missing resume is the entire difference, and it is what makes the new conversation know nothing.
    /// <para>
    /// The transcript is kept and marked with a divider (decision 1): it is the pane's audit surface, and a line
    /// showing exactly where the agent's memory stops is more use than an empty window. The old conversation is
    /// untouched on disk and stays resumable under its own id — the new one simply has a different id, which is
    /// what the caller's confirmation says before any of this runs (decision 2).
    /// </para>
    /// </summary>
    public async Task ClearContextAsync(SessionProfile profile)
    {
        if (_runtime is null)
        {
            return;
        }

        // A turn parked on a permission prompt has to be answered before its driver goes away, or the pane goes
        // on asking for attention over a decision nothing is waiting for — half of the half-state AC-564 calls
        // out. The tool never ran, and IsPendingPermission is what the chip and the status actually read.
        // The running turn itself needs nothing here: _StopRuntimeAsync tears down through the runtime, which
        // interrupts before it takes the process away (SessionRuntime.DisposeAsync) — a second interrupt from
        // this side would only be the same call again.
        foreach (var pending in Transcript.Where(entry => entry.IsPendingPermission).ToList())
        {
            pending.PermissionDecision = "Cancelled — context cleared";
            pending.IsPendingPermission = false;
        }

        await _StopRuntimeAsync();
        _ResetForNewConversation();

        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.Divider, "Context cleared — a new conversation starts here"));

        // The live selections rather than the launch values: a session whose model or reading level was switched
        // mid-flight carries on as the operator last left it. Pre-approved tools ride along too, so clearing the
        // context of a self-driving run (AC-215) does not quietly demote it into one that stops to ask.
        await StartConfiguredAsync(
            profile,
            SelectedPermissionMode,
            SelectedModel,
            SelectedEffort,
            McpServerSelection,
            WorkingDirectory,
            resume: null,
            _launchOptions,
            ReadingLevel,
            _preApprovedTools.ToList(),
            _preApproveAllTools);
    }

    // Everything that described the conversation just dropped (AC-564). The turn's live state and the queue aimed
    // at it go because the turn they belonged to is over; the numbers go because "ctx 66%" left standing over an
    // empty context is a figure that actively lies (decision 3). The transcript is deliberately not among them.
    private void _ResetForNewConversation()
    {
        QueuedMessages.Clear();
        if (_activeToolCalls.Count > 0)
        {
            _activeToolCalls.Clear();
            _RaiseActiveToolActivityChanged();
        }

        _subAgentLanes.Clear();
        _currentTurnAssistantEntries.Clear();
        _currentAssistantEntry = null;
        _currentOrphanedSubAgentTextEntry = null;
        _readAloudFlushedLength = 0;
        _spokenSomethingThisTurn = false;
        _StopSignOfLifeClock();
        ClearCurrentTurnImages();
        _hasCompletedATurn = false;
        _needsAttention = false;
        IsBusy = false;

        _usage.Reset();
        HasUsage = _usage.HasData;
        UsageSummary = string.Empty;
        UsageTooltip = string.Empty;
        ContextUsedPercent = null;
        RateLimits.Clear();
        LimitsTooltip = string.Empty;

        // A restore offer belongs to the conversation this pane was restored with; that conversation is no longer
        // the one running here, so the banner must not go on offering to resume it.
        RestoreOffer = null;
        _RecomputeStatus();
    }

    /// <summary>
    /// The header kind chip's label for a profile's provider (AC-537): a built-in provider's own label, nothing
    /// for a plain Claude SDK session (the chip then falls back to "SDK"), and — for a Plugin-provider profile —
    /// the specific plugin's own display name, resolved through <see cref="_pluginProviderRegistry"/> the same
    /// way the New-session profile picker resolves it (<see cref="Converters.ProfileDisplayConverter"/>) rather
    /// than the generic "Plugin" placeholder <see cref="SessionProviderCatalog"/> falls back to when it cannot
    /// tell one plugin provider from another. No registry, or nothing registered under the profile's provider
    /// id, yields no label at all — a placeholder that names nothing is worse than no chip. A registered but
    /// blank/whitespace-only display name (a plugin author's own mistake, measured while proving this out) is
    /// treated the same as nothing resolved, rather than showing a technically-visible, actually-empty chip.
    /// </summary>
    private string _ResolveProviderBadge(SessionProfile? profile)
    {
        if (profile?.Provider is null or SessionProvider.ClaudeCli)
        {
            return string.Empty;
        }

        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            return SessionProviderCatalog.Resolve(profile.Provider).Label;
        }

        var name = _pluginProviderRegistry?.Resolve(plugin.ProviderId)?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    private async Task StartWithProfileAsync(SessionProfile? profile, string? workingDirectory = null, SessionResume? resume = null)
    {
        if (_sessionManager is null)
        {
            return;
        }

        ProviderBadge = _ResolveProviderBadge(profile);
        // The shared header's kind chip (AC-37): the provider tag, or "SDK" for a plain Claude SDK session.
        KindLabel = string.IsNullOrEmpty(ProviderBadge) ? "SDK" : ProviderBadge;
        // Set before the launch is even attempted, not after it succeeds (AC-545 follow-up): the label is a fact
        // about what was launched, not about whether it came up, so a launch that throws inside the try below must
        // not leave this session looking never-launched (empty profile) forever. Matches
        // TtyViewModel.LaunchConfigured, which sets it before TryRaiseLaunch() for the same reason.
        ActiveProfileLabel = profile?.Label;

        // A per-session working directory override reflects immediately on the shared base (so the header and
        // the read/observe surface show where this session runs) even before the CLI's own init event confirms
        // its cwd; a blank override leaves it to be filled from that init event as before.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            WorkingDirectory = workingDirectory;
        }

        IsStarting = true;
        Status = "Starting...";

        try
        {
            // The runtime owns the driver and the event pump (#68); this panel subscribes to its events and
            // marshals them onto the UI thread itself. Inside the try: a profile referencing a missing or
            // unresolvable plugin provider (or an invalid persisted ConfigJson) throws during the runtime's
            // start — catching it degrades to the existing failed-launch path (Status set, no running runtime)
            // instead of an unhandled throw stranding the panel that CockpitViewModel already added.
            var runtime = _sessionManager.Create(profile);
            runtime.EventAppended += _OnSessionEvent;
            _runtime = runtime;

            // The session's working life starts here, not when the panel was constructed — whatever the launch
            // waited on (resolving a worktree, a profile) is setup, not work (AC-251).
            _startedAt = DateTimeOffset.Now;

            // The model dropdown lists Claude aliases (opus/sonnet/…), which are meaningless to a local
            // provider — it uses the model set on its profile. Only pass the selected model for Claude, so
            // a local session keeps its own configured model instead of being clobbered with "opus".
            var launchModel = profile?.Provider is null or SessionProvider.ClaudeCli ? SelectedModel.Value : null;
            // AC-218: ProjectId is set on this panel by CockpitViewModel before StartConfiguredAsync runs, so it is
            // already current here — passed through so the driver's MCP fan-out resolves this project's registry view.
            await runtime.StartAsync(profile, SelectedPermissionMode.Value, launchModel, McpServerSelection, workingDirectory, resume, _launchOptions, ProjectId);

            // The process the meter weighs (#78) exists only once the driver started it.
            ProcessId = runtime.ProcessId;

            // Capabilities (notably SupportsTools) only settle once the driver has actually started — the
            // local (OpenAI-compatible) driver's SupportsTools flips true only after its MCP tool session
            // connects during StartAsync — so read them here rather than right after Create(), which would
            // always see the driver's pre-start (all-false) defaults.
            // The runtime only knows them once its driver is up, which it now is.
            if (runtime.Capabilities is { } capabilities)
            {
                Capabilities = capabilities;
            }

            // The provider's generic live controls (#45 D4) settle at the same moment as capabilities — the driver
            // lists them once its session is up (Codex resolves its model list on start) — so read them here too.
            _PopulateLiveControls();

            OnPropertyChanged(nameof(CanPasteImages));
            // A local tool session gates via the per-call approval prompt (not Claude's permission modes), so it
            // gets the "Allow all tools" convenience toggle; Claude uses its own permission mode dropdown.
            var isLocalToolSession = Capabilities is { SupportsTools: true, SupportsPermissions: false };
            ShowToolAutoApprove = isLocalToolSession;

            // A profile marked "auto-approve tools" (#26) seeds the toggle for a fresh local tool session, so
            // it starts already on instead of needing the operator to flip it every time for a profile they
            // trust. wasAlreadyOn distinguishes that from a choice the operator flipped before the session
            // finished starting: assigning the property below only calls the driver (through
            // OnAutoApproveToolsChanged) when the value actually changes, i.e. exactly the freshly-seeded
            // case — the pre-set case needs its own explicit re-apply just after, since any hook call at
            // flip-time hit a session that wasn't running yet.
            var wasAlreadyOn = AutoApproveTools;
            AutoApproveTools = AutoApproveTools || (isLocalToolSession && profile?.Defaults?.AutoApproveTools == true);

            if (AutoApproveTools && wasAlreadyOn)
            {
                await runtime.SetAutoApproveToolsAsync(true);
            }

            // ActiveProfileLabel is already set (before the launch attempt, above); the profile is shown
            // separately from Status, so keep the status itself clean rather than repeating it —
            // "Session started. · personal" read as a duplicate (L6).
            Status = "Session started.";
            // The runtime is up: stop signalling "still starting". IsSessionReady is refreshed by the single
            // caller (StartConfiguredAsync) right after this returns, so it is not raised again here.
            IsStarting = false;

            // Thinking budget has no launch flag — the control request is the only path — so apply
            // the selected effort once the session is live, otherwise it runs at the CLI default
            // until the operator first touches the dropdown.
            await _SetMaxThinkingTokensSafeAsync(SelectedEffort.MaxThinkingTokens);
        }
        catch (Exception ex)
        {
            Status = $"Failed to start: {ex.Message}";
            // The launch failed — clear the "still starting" banner so it does not sit there implying the
            // session is about to come up. IsSessionReady stays false (no running runtime); the caller settles it.
            IsStarting = false;
        }
    }

    /// <summary>Live-toggles auto-approval of tool calls on the running session's driver (local sessions).</summary>
    partial void OnAutoApproveToolsChanged(bool value)
    {
        _ = _runtime?.SetAutoApproveToolsAsync(value);
    }

    /// <summary>Live-switches the running session's permission mode. No-op before the session has started.</summary>
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    /// <summary>Live-switches the running session's model. No-op before the session has started.</summary>
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetModelSafeAsync(value.Value);
    }

    /// <summary>
    /// Applies the edited Claude model as a live switch, called by the view when the model field commits (Enter,
    /// focus-loss, or picking a suggestion). Routes through <see cref="SelectedModel"/> so the model of record and
    /// the live control request (via <see cref="OnSelectedModelChanged"/>) stay one path; a blank field or an
    /// unchanged value is ignored so a commit that changed nothing fires no request.
    /// </summary>
    public void CommitLiveModel()
    {
        var text = LiveModelText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var model = SessionOptionCatalog.ModelForValue(text);
        if (model.Value != SelectedModel.Value)
        {
            SelectedModel = model;
        }
    }

    /// <summary>Live-switches the running session's thinking budget. No-op before the session has started.</summary>
    partial void OnSelectedEffortChanged(EffortOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetMaxThinkingTokensSafeAsync(value.MaxThinkingTokens);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        try
        {
            await _runtime.InterruptAsync();
            Status = "Interrupted.";
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Interrupt failed: {ex.Message}"));
        }
    }

    private async Task _SetPermissionModeSafeAsync(string mode)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetPermissionModeAsync(mode);

            // AC-409: only once the switch actually took — a failed one leaves the running session on its old
            // mode, and recording the requested one anyway would tell a restart to bring back a mode this session
            // never ran under.
            _ = _sessionStateRecorder?.RecordPermissionModeChangedAsync(PaneId, mode);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Permission-mode switch failed: {ex.Message}"));
        }
    }

    private async Task _SetModelSafeAsync(string model)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Model switch failed: {ex.Message}"));
        }
    }

    private async Task _SetMaxThinkingTokensSafeAsync(int maxThinkingTokens)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetMaxThinkingTokensAsync(maxThinkingTokens);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Effort switch failed: {ex.Message}"));
        }
    }

    /// <summary>Rebuilds the generic live-control panel from the running driver's declared options (#45 D4).</summary>
    private void _PopulateLiveControls()
    {
        LiveControls.Clear();
        if (_runtime is null)
        {
            return;
        }

        foreach (var option in _runtime.LiveOptions)
        {
            LiveControls.Add(new LiveControlViewModel(option, _SetLiveOptionSafeAsync));
        }
    }

    /// <summary>Live-switches one of the provider's generic controls on the running session's driver (#45 D4).</summary>
    private async Task _SetLiveOptionSafeAsync(string key, string value)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetLiveOptionAsync(key, value);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Live switch failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Queues a pasted image as a pending attachment for the next message. Called from the view's
    /// CTRL+V handler, which owns the Avalonia clipboard read; the view model only sees PNG bytes so
    /// it stays free of UI-toolkit types and unit-testable.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="CanPasteImages"/> (#64): the CTRL+V gesture has no button to hide, so a session
    /// whose driver would otherwise silently drop the image (<see cref="SessionCapabilities.SupportsVision"/>
    /// false — today's Ollama/LM Studio/plugin sessions) gets a transcript notice instead of a queued
    /// attachment that vanishes unsent.
    /// </remarks>
    public void AddPastedImage(byte[] pngBytes)
    {
        if (!CanPasteImages)
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, "This session's provider does not support image input — the pasted image was not attached."));
            return;
        }

        PendingAttachments.Add(new ImageAttachmentViewModel(pngBytes, a => PendingAttachments.Remove(a)));
    }

    /// <summary>
    /// Appends a finished voice transcript to the input box rather than sending it straight away, so
    /// the operator can proofread the STT/cleanup result before pressing Enter — the SDK session
    /// already has a text input surface, so this reuses it instead of adding a separate send path.
    /// </summary>
    protected override void OnVoiceTextReady(string text) =>
        InputText = string.IsNullOrEmpty(InputText) ? text : $"{InputText} {text}";

    /// <summary>
    /// Queues a captured screenshot (AC-220) as a pending attachment, the same chip a CTRL+V paste produces —
    /// so the operator can type a sentence with it and send when they mean to, rather than the image being shot
    /// off on its own. Deliberately no auto-submit: a screenshot is nearly always "look at this, because…".
    /// </summary>
    /// <remarks>
    /// The vision gate is <see cref="ScreenshotKindRefusal"/>'s and is checked before this runs, rather than left
    /// to <see cref="AddPastedImage"/> — which answers a non-vision provider with a transcript row of its own,
    /// and would mean telling the operator twice.
    /// </remarks>
    protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng)
    {
        PendingAttachments.Add(new ImageAttachmentViewModel(screenshotPng, a => PendingAttachments.Remove(a)));
        return Task.FromResult<string?>(null);
    }

    /// <summary>A provider that never builds an image block would take the attachment and leave without it — so the button is off and the key says why.</summary>
    protected override string? ScreenshotKindRefusal =>
        CanPasteImages ? null : "This session's provider does not support image input, so the screenshot was not attached.";

    /// <summary>Auto-submit: sends the input box the transcript was just appended to — the same path Enter/Send takes, so a busy session queues it (T8) rather than erroring.</summary>
    protected override void OnVoiceSubmitRequested()
    {
        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    /// <summary>
    /// Shows a verify screenshot (AC-86) as a real user turn, captioned, only when this provider can see images
    /// (<see cref="CanPasteImages"/>) — the same vision gate a pasted image passes through; the text snapshot already
    /// reached the agent on the tool result. A turn already in flight queues it (T8), so it lands as the next user
    /// turn rather than erroring against the mid-turn input the CLI rejects. Returns whether the screenshot was shown.
    /// </summary>
    public override async Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng)
    {
        if (_runtime is not { IsRunning: true } || !CanPasteImages)
        {
            return false;
        }

        IReadOnlyList<Core.Sessions.ImageAttachment> images = [Core.Sessions.ImageAttachment.FromBytes(screenshotPng, "image/png")];

        if (IsBusy)
        {
            QueuedMessages.Add(new QueuedMessageViewModel(caption, images, message => QueuedMessages.Remove(message)));
            return true;
        }

        await _DispatchMessageAsync(caption, images);
        return true;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
        {
            return;
        }

        // Sending before the session has started reaches the CLI process before its I/O is wired and
        // surfaces a raw "Start must be called before I/O" error (#16). Post-#31 a session starts as
        // soon as it is created, so this only bites a failed-to-start panel — guard it with a plain
        // message and keep the typed text rather than clearing it into a raw error. The driver itself is
        // only created once the session starts (#26), so a null session means "not started" too. Queued
        // dispatch never lands here: a queue only exists once a turn was in flight, i.e. after a start.
        if (_runtime is not { IsRunning: true })
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, "The session has not started yet — nothing was sent."));
            return;
        }

        var text = InputText;
        var images = PendingAttachments
            .Select(a => Core.Sessions.ImageAttachment.FromBytes(a.PngBytes, a.MediaType))
            .ToList();

        InputText = string.Empty;
        PendingAttachments.Clear();

        // The CLI rejects mid-turn input, so while a turn is in flight the message goes onto the local
        // send queue as a cancellable chip and is dispatched when the turn completes (T8), instead of
        // being blocked or silently dropped. The echo row is added at dispatch time so the transcript
        // stays in send order.
        if (IsBusy)
        {
            QueuedMessages.Add(new QueuedMessageViewModel(text, images, m => QueuedMessages.Remove(m)));
            return;
        }

        await _DispatchMessageAsync(text, images);
    }

    /// <summary>
    /// Pulls the most recently queued message back into the input for editing (Arrow Up on an empty
    /// input) — its text and any images are restored and the chip is removed. Returns false when the
    /// queue is empty, so the key handler can let Arrow Up do its normal thing.
    /// </summary>
    public bool RecallLastQueuedMessage()
    {
        if (QueuedMessages.Count == 0)
        {
            return false;
        }

        var last = QueuedMessages[^1];
        QueuedMessages.RemoveAt(QueuedMessages.Count - 1);

        InputText = last.Text;
        foreach (var image in last.Images)
        {
            AddPastedImage(Convert.FromBase64String(image.Base64Data));
        }

        return true;
    }

    /// <summary>Sends a message to the session now, echoing it into the transcript and marking the turn busy.</summary>
    private async Task _DispatchMessageAsync(string text, IReadOnlyList<Core.Sessions.ImageAttachment> images)
    {
        if (_runtime is null)
        {
            return;
        }

        // "exit" closes the session once its turn completes when the operator enabled it (T10). The
        // message is still sent normally so any session-end/Stop-hooks on Claude's side run first; the
        // close then fires from the TurnCompleted handler. Armed at dispatch so a queued "exit" counts too.
        if (AutoCloseOnExit && text.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            _closeAfterTurn = true;
        }

        var imageSuffix = images.Count == 0
            ? string.Empty
            : $"[+{images.Count} image{(images.Count == 1 ? "" : "s")}]";
        var echo = string.IsNullOrEmpty(text)
            ? imageSuffix
            : images.Count == 0 ? text : $"{text}  {imageSuffix}";
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, echo));
        _currentAssistantEntry = null;
        _CloseThinkingRow();
        IsBusy = true;
        _needsAttention = false;
        _RecomputeStatus();

        // Remember this message's images as the turn's images (AC-116) before the send, so a tool result that
        // races ahead of this method's continuation still sees them; a plugin reacting to a tool call later in the
        // turn — a YouTrack tracker attaching them to an issue the agent just created — reads exactly this turn's
        // images off the read/observe surface. Cleared when the turn completes, or here if the send never happened.
        _RememberTurnImages(images);

        try
        {
            await _SendWithWaitingMessagesAsync(_runtime, text, images, _NoteDeliveredMail);
        }
        catch (Exception ex)
        {
            ClearCurrentTurnImages();
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Send failed: {ex.Message}"));
            IsBusy = false;
            _RecomputeStatus();
        }
    }

    /// <summary>
    /// The one place this pane hands a turn to its runtime, so that turn-start delivery (AC-394) cannot be reached
    /// by one send path and missed by another — <c>SessionViewModelSendPathTests</c> holds it to that. Messages
    /// waiting for this pane ride out ahead of <paramref name="text"/>, as a block that says where they came from;
    /// with nothing waiting the runtime is handed the very same string it would have been handed before, so an idle
    /// desk adds no tokens to any turn.
    /// </summary>
    private async Task _SendWithWaitingMessagesAsync(
        ISessionRuntime runtime,
        string text,
        IReadOnlyList<Core.Sessions.ImageAttachment>? images,
        Action<AgentInboxTurnNotice>? note = null)
    {
        // Only a runtime that is actually running can carry a turn, and "did not throw" is not enough to tell.
        // A runtime whose driver never came up — a profile naming a provider that fails to resolve leaves one
        // behind, and the pane keeps holding it — accepts a send and hands back a completed task with nothing
        // having gone anywhere. Taking mail for that turn would then confirm a delivery that never happened and
        // drop the messages for good, with every sender having been told they arrived: the exact loss the rest of
        // this handshake is built to make impossible. So the mail is only taken once the turn can leave.
        var waiting = runtime.IsRunning ? _turnInboxDelivery?.TakeForTurn(PaneId) : null;

        try
        {
            // Rendering is inside the try along with the send, not before it: once TakeForTurn has run, the messages
            // are held in flight and something has to say which way they went. A throw between the taking and the
            // try would leave them held for the life of the pane — counted against its inbox cap, invisible to
            // read_inbox, and freed only when the session closes.
            var outgoing = waiting is null ? text : $"{waiting.Render()}\n\n{text}";
            await runtime.SendUserMessageAsync(outgoing, images);
        }
        catch
        {
            // The turn never left, so neither did the mail. Put it back before the failure travels on, or a send that
            // failed would have quietly consumed messages the recipient never saw and the sender was told had
            // arrived — the one outcome this line is built to prevent.
            if (waiting is not null)
            {
                _turnInboxDelivery?.ReturnUndelivered(waiting);
            }

            throw;
        }

        if (waiting is not null)
        {
            note?.Invoke(waiting);
            _turnInboxDelivery?.ConfirmDelivered(waiting);
        }
    }

    /// <summary>
    /// Puts a note in the transcript that a peer's mail rode out on this turn (AC-394), in the same bracketed form
    /// the turn's images already use.
    /// <para>
    /// It exists because this is the first text that enters a session's context which the operator neither typed nor
    /// can see. The route it replaces — the agent calling <c>read_inbox</c> — was a tool call, and a tool call is a
    /// transcript row: mail arriving was visible, and so was its content. Without this the agent answers something
    /// that is not in the transcript, and the operator reads the reply as though their own sentence had prompted it.
    /// </para>
    /// <para>
    /// The note says that mail arrived and from where, not what it said. The bodies are another agent's prose, up to
    /// a few thousand characters of it, and inlining that into the operator's own row would drown the sentence they
    /// wrote. Showing the bodies in full belongs with a transcript row of their own, which is a larger change than
    /// this ticket — what matters here is that the operator can no longer be surprised by an answer with no visible
    /// question.
    /// </para>
    /// </summary>
    private void _NoteDeliveredMail(AgentInboxTurnNotice notice)
    {
        var senders = notice.Messages
            .Select(message => message.FromPaneId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var from = senders.Count == 1 ? senders[0] : $"{senders.Count} other sessions";
        var count = notice.Messages.Count == 1 ? "1 message" : $"{notice.Messages.Count} messages";

        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.UserText,
            $"[{count} from {from} delivered with this turn]"));
    }

    // Records the message's images as the current turn's images (AC-116), provider-agnostic, for the read/observe
    // surface to hand to a plugin that reacts to a tool call this turn. A no-op with no images.
    private void _RememberTurnImages(IReadOnlyList<Core.Sessions.ImageAttachment> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        var attachments = images
            .Select((image, index) => new SessionImageAttachment(
                image.MediaType,
                image.Base64Data,
                $"pasted-image-{index + 1}.{_ImageExtension(image.MediaType)}"))
            .ToList();

        SetCurrentTurnImages(attachments);
    }

    private static string _ImageExtension(string mediaType)
    {
        var subtype = mediaType.Split('/').LastOrDefault() ?? string.Empty;
        // A compound subtype (image/svg+xml) or one with parameters (…;charset=…) must not leak "+xml"/";…" into
        // the file name.
        var clean = subtype.Split('+', ';')[0].Trim();

        return clean.Length > 0 ? clean : "png";
    }

    /// <summary>
    /// Dispatches the next queued message (T8) once a turn frees the session. Fire-and-forget: the
    /// synchronous part of the dispatch flips <see cref="IsBusy"/> back on before the first await, so
    /// the status settles immediately. No-op when the queue is empty.
    /// </summary>
    private void _TryDispatchNextQueued()
    {
        if (QueuedMessages.Count == 0)
        {
            return;
        }

        // Combine mode (AC-145): drain the whole queue into one follow-up turn so the agent sees every queued
        // message at once, instead of answering each as its own turn. Texts join with a blank line between them
        // (empties — image-only chips — are dropped from the text); images carry over in queue order and land as
        // one echo row via _DispatchMessageAsync. Consequence: a queued "exit" merged with other text no longer
        // auto-closes (the combined text is not exactly "exit"); a lone queued "exit" is a count of 1, so it falls
        // through to the single-dispatch path below and still closes as before.
        if (CombineQueuedMessages && QueuedMessages.Count > 1)
        {
            var combinedText = string.Join(
                "\n\n", QueuedMessages.Select(m => m.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            var combinedImages = QueuedMessages.SelectMany(m => m.Images).ToList();
            QueuedMessages.Clear();
            _ = _DispatchMessageAsync(combinedText, combinedImages);
            return;
        }

        var next = QueuedMessages[0];
        QueuedMessages.RemoveAt(0);
        _ = _DispatchMessageAsync(next.Text, next.Images);
    }

    [RelayCommand]
    private async Task AllowToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: true);
    }

    [RelayCommand]
    private async Task DenyToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: false);
    }

    /// <summary>Allows the call and persists a rule matching only this exact tool + input for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysExactToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Exact);
    }

    /// <summary>Allows the call and persists a rule matching every future call to this tool for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysWildcardToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Wildcard);
    }

    private async Task RespondToPermissionAsync(TranscriptEntryViewModel entry, bool allow)
    {
        if (_runtime is null || entry.ToolUseId is null)
        {
            return;
        }

        entry.PermissionDecision = allow ? "Allowed" : "Denied";
        entry.IsPendingPermission = false;
        // AC-532: the operator's decision may be what the composer's activity band was showing "waiting for
        // permission" for — re-raise so it reverts to the normal running text (or goes quiet, if this was the
        // call's only reason to still be shown).
        _RaiseActiveToolActivityChanged();
        await _runtime.RespondToPermissionAsync(entry.ToolUseId, allow);
    }

    private async Task AllowAlwaysAsync(TranscriptEntryViewModel entry, PermissionRuleScope scope)
    {
        if (_runtime is null || entry.ToolUseId is null || entry.ToolName is null)
        {
            return;
        }

        entry.PermissionDecision = scope == PermissionRuleScope.Wildcard
            ? $"Always allowed ({entry.ToolName}:*)"
            : $"Always allowed (exact: {entry.ToolName})";
        entry.IsPendingPermission = false;
        // AC-532: see RespondToPermissionAsync above — this decision can end the composer's "waiting for
        // permission" text too.
        _RaiseActiveToolActivityChanged();

        await _runtime.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);
    }

    /// <summary>
    /// Enqueues the assistant prose accumulated since the last flush (#35, AC-97). Called both when the turn
    /// finishes and when it pauses on a question/permission prompt mid-turn — so the lead-in a reply gives before
    /// asking ("let me check…") is spoken right away instead of staying silent until the operator answers. The
    /// flushed-count marks each entry spoken exactly once, no matter how many prompts one turn raises.
    /// </summary>
    private void _FlushPendingProseForReadAloud()
    {
        if (!ReadResponsesAloud)
        {
            return;
        }

        // Only the last entry grows (deltas append to the current one), so the prose up to _readAloudFlushedLength
        // is stable and the tail from there is exactly what has not been spoken yet.
        var prose = string.Join("\n\n", _currentTurnAssistantEntries.Select(entry => entry.Text));
        if (_readAloudFlushedLength >= prose.Length)
        {
            return;
        }

        var pending = prose[_readAloudFlushedLength..];
        _readAloudFlushedLength = prose.Length;
        _spokenSomethingThisTurn = true;
        _RestartSignOfLifeClock();
        _ = EnqueueReadAloudAsync(pending);
    }

    /// <summary>
    /// Says out loud that it is about to go and look, when the model went straight to a tool without saying so
    /// (AC-597). The assistant's own session only: an ordinary pane speaks its replies, it does not chat.
    /// </summary>
    /// <remarks>
    /// The standing instruction asks for this lead-in and gets one about three turns in five. The rest go quiet
    /// from the question until the whole answer is ready, which sounds exactly like not having been heard.
    /// </remarks>
    internal void _SpeakLeadInIfTheModelGaveNone()
    {
        if (!ReadResponsesAloud || _spokenSomethingThisTurn || !IsTheVoiceAssistant)
        {
            return;
        }

        var filler = AssistantSpokenFillers.GoingToLookUpSomething(ReadAloudLanguage, _spokenFillerRotation++);
        if (filler.Length == 0)
        {
            return;
        }

        _spokenSomethingThisTurn = true;
        _RestartSignOfLifeClock();
        _ = EnqueueReadAloudAsync(filler);
    }

    /// <summary>True for the cockpit's own voice assistant, the one session that speaks unasked (AC-597/598).</summary>
    private bool IsTheVoiceAssistant =>
        string.Equals(PaneId, AssistantIdentity.PaneId, StringComparison.Ordinal);

    /// <summary>
    /// Starts the clock that says "still on it" while a turn keeps running (AC-598), and pushes it back every time
    /// something real is spoken.
    /// </summary>
    private void _RestartSignOfLifeClock()
    {
        if (!ReadResponsesAloud || !IsTheVoiceAssistant)
        {
            return;
        }

        _signOfLifeTimer ??= _BuildSignOfLifeTimer();
        _signOfLifeTimer.Stop();
        _signOfLifeTimer.Interval = AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat);
        _signOfLifeTimer.Start();
    }

    private DispatcherTimer _BuildSignOfLifeTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            // A turn that ended, or one stopped on a permission: the first has nothing to report and the second
            // already said out loud that it is waiting. Speaking over either is noise.
            if (!IsBusy || HasPendingPermission || PendingConsent is not null)
            {
                _StopSignOfLifeClock();
                return;
            }

            var filler = AssistantSpokenFillers.StillAtIt(ReadAloudLanguage, _signOfLifeRepeat);
            _signOfLifeRepeat++;
            if (filler.Length > 0)
            {
                _ = EnqueueReadAloudAsync(filler);
            }

            timer.Interval = AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat);
        };

        return timer;
    }

    private void _StopSignOfLifeClock()
    {
        _signOfLifeTimer?.Stop();
        _signOfLifeRepeat = 0;
    }

    // The runtime pumps the driver off the UI thread and raises each event here (#68); marshalling onto the UI
    // thread is this panel's job, because it is the consumer that touches UI — a headless consumer of the same
    // runtime marshals nothing.
    //
    // AC-529: through the queue rather than a post per event. A streaming turn raises hundreds of few-character
    // deltas, and one post each meant one full re-realisation of the row's text and one re-measure each; the queue
    // hands the UI thread whatever piled up since the last drain, with adjacent deltas folded into one. Order and
    // content are unchanged — see SessionEventQueue for why nothing is left behind at the end of a turn.
    private void _OnSessionEvent(SessionEvent evt) => _eventQueue.Enqueue(evt);

    private readonly SessionEventQueue _eventQueue;

    /// <summary>
    /// Raised when the session makes real tool progress — a tool call surfacing or a tool result landing (AC-215/stall).
    /// An embedder that fails a silent step on a stall deadline (Autopilot) resets that deadline on this, so a step that
    /// is slow because it is working hard is not mistaken for a stuck one. Not raised on text/thinking on purpose.
    /// </summary>
    public event Action? ToolActivity;

    /// <summary>internal (rather than private) so <c>Cockpit.Core.Tests</c> can drive it directly, bypassing <c>Dispatcher.UIThread</c> — see <see cref="_OnSessionEvent"/>.</summary>
    internal void Apply(SessionEvent evt)
    {
        // No per-event bookkeeping for the "Thinking…" band any more (AC-532 round 2). It used to track "no visible
        // output yet" — cleared by the first text, re-armed only by a ToolResult — and that is what left the
        // composer blank for a minute at a time when the model said something and then went back to work. It now
        // reads IsBusy, which the turn's own start and end already maintain; see ShowThinkingIndicator.

        // Real tool progress (AC-215/stall): a tool call surfacing or a tool result landing is the agent actually
        // working — the signal that distinguishes a busy-but-progressing step from a genuinely stuck one (AC-192: a
        // turn that emits text describing a tool it never runs, so no tool event ever fires). An embedder that fails a
        // silent step on a stall deadline (Autopilot) resets that deadline on this, so a long, hard-working step is not
        // failed for being slow. Deliberately NOT raised on text/thinking — a stuck agent still produces those.
        if (evt is ToolUseRequested or ToolResult)
        {
            ToolActivity?.Invoke();
        }

        switch (evt)
        {
            case SessionInitialized init:
                // The init event is where an SDK session's working directory becomes known — surface it on the
                // shared base so the read/observe surface can report it (a directory-scoped plugin follows this).
                if (!string.IsNullOrEmpty(init.Cwd))
                {
                    WorkingDirectory = init.Cwd;
                }

                // AC-537: the tool count said nothing an operator could act on, and cwd duplicated the folder
                // icon's own tooltip (SessionHeaderBar.axaml). The MCP-server count is the one figure here that
                // actually describes the session's setup. Read off McpServerSelection, which is also what the
                // activity column's hover lists (AC-563) — one fact, two readings.
                Status = ConnectedStatusLine;
                // AC-563 took the tool names off the provider chip's hover — the same count AC-537 had already
                // ruled uninformative, one hover further along. The heading itself stays: the empty-state card
                // (SessionView.axaml) introduces a fresh session with it, where "no tools connected" is the one
                // thing worth saying before anything has happened.
                ConnectedToolsHeading = init.Tools.Count == 0
                    ? "No tools connected — add an MCP server (e.g. filesystem) to give this session tools."
                    : $"{init.Tools.Count} tools connected";

                // AC-141: a session launched with no explicit model (Auto/default) built its Model live-control
                // with nothing to show — the init event is the one place the CLI states which model it actually
                // picked. Seed it in, don't fire a switch: the driver already reported this, and set_model would
                // be the host talking back a choice the operator never made.
                if (init.Model is { Length: > 0 } resolvedModel)
                {
                    foreach (var control in LiveControls)
                    {
                        if (control.Key == WellKnownPluginSessionOptions.Model)
                        {
                            control.SeedIfUnset(resolvedModel);
                        }
                    }
                }

                break;

            case AssistantTextDelta delta:
                // AC-146: a sub-agent's own streaming text accumulates onto its lane's row, nested under its
                // Task tool-use anchor rather than into the top-level transcript — the operator's own reply and
                // a sub-agent's internal narration must never merge into one row.
                if (_ResolveSubAgentLane(delta.ParentToolUseId) is { } textLane)
                {
                    textLane.CurrentThinkingEntry = null;
                    if (textLane.CurrentAssistantEntry is null)
                    {
                        textLane.CurrentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                        textLane.Anchor.SubAgentRows.Add(textLane.CurrentAssistantEntry);
                    }

                    textLane.CurrentAssistantEntry.AppendText(delta.Text);
                    break;
                }

                // AC-146: a parent id this pane never resolved to a lane (the anchor tool-use row was never
                // seen) is an orphan, not a top-level chunk — shown, in its own separate entry so it can never
                // merge into whatever the genuine top-level reply is doing, but never queued for read-aloud.
                if (!string.IsNullOrEmpty(delta.ParentToolUseId))
                {
                    if (_currentOrphanedSubAgentTextEntry is null)
                    {
                        _currentOrphanedSubAgentTextEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                        Transcript.Add(_currentOrphanedSubAgentTextEntry);
                    }

                    _currentOrphanedSubAgentTextEntry.AppendText(delta.Text);
                    break;
                }

                // Visible prose has started, so the reasoning block that preceded it is done: close the thinking
                // row (AC-213) so a later thinking block opens a fresh row instead of appending onto this one.
                _CloseThinkingRow();
                if (_currentAssistantEntry is null)
                {
                    _currentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                    Transcript.Add(_currentAssistantEntry);
                    _currentTurnAssistantEntries.Add(_currentAssistantEntry);
                }

                _currentAssistantEntry.AppendText(delta.Text);
                break;

            case AssistantTextCompleted completed:
                // A sub-agent's completed-text snapshot (some providers send one instead of streaming deltas)
                // lands on its own lane the same way the streaming path above does, and never joins the
                // top-level read-aloud queue below.
                if (_ResolveSubAgentLane(completed.ParentToolUseId) is { } completedLane)
                {
                    completedLane.CurrentThinkingEntry = null;
                    if (completedLane.CurrentAssistantEntry is not null)
                    {
                        completedLane.CurrentAssistantEntry = null;
                    }
                    else
                    {
                        completedLane.Anchor.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text));
                    }

                    break;
                }

                // AC-146: same orphan handling as the streaming case above — never queued for read-aloud, never
                // raises the output-text signal below.
                if (!string.IsNullOrEmpty(completed.ParentToolUseId))
                {
                    if (_currentOrphanedSubAgentTextEntry is not null)
                    {
                        _currentOrphanedSubAgentTextEntry = null;
                    }
                    else
                    {
                        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text));
                    }

                    break;
                }

                _CloseThinkingRow();
                if (_currentAssistantEntry is not null)
                {
                    // Streaming deltas already built the text; nothing further to append.
                    _currentAssistantEntry = null;
                }
                else
                {
                    var completedEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text);
                    Transcript.Add(completedEntry);
                    _currentTurnAssistantEntries.Add(completedEntry);
                }

                // Assistant prose is one of the two channels a plugin watches for an output signal (the other
                // is tool output below) — e.g. Claude announcing "opened https://github.com/…/pull/5". A
                // sub-agent's own narration is not the session's answer to the operator, so it never reaches
                // this signal either (kept inside the branch above).
                RaiseOutputText(completed.Text);
                break;

            case ToolUseRequested toolUse:
                // AC-146: a sub-agent's own tool call nests under its Task row instead of flattening into the
                // top-level transcript — this is also how a sub-agent's tool call becomes an anchor a *further*
                // nested lane could resolve against, though today's CLI only nests one level deep.
                if (_ResolveSubAgentLane(toolUse.ParentToolUseId) is { } toolUseLane)
                {
                    toolUseLane.CurrentAssistantEntry = null;
                    toolUseLane.CurrentThinkingEntry = null;
                    toolUseLane.Anchor.SubAgentRows.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.ToolUse,
                        $"Tool: {toolUse.ToolName}({toolUse.InputJson})")
                    {
                        ToolUseId = toolUse.ToolUseId,
                        ToolName = toolUse.ToolName,
                        InputJson = toolUse.InputJson,
                    });
                    break;
                }

                // Close the current assistant text row so prose that streams *after* this tool call starts a
                // fresh row beneath the tool, in the order it happened — otherwise post-tool text appends back
                // onto the pre-tool row and the whole reply collapses above the tools it actually followed.
                _currentAssistantEntry = null;
                _CloseThinkingRow();
                var toolUseRow = new TranscriptEntryViewModel(
                    TranscriptEntryKind.ToolUse,
                    $"Tool: {toolUse.ToolName}({toolUse.InputJson})")
                {
                    ToolUseId = toolUse.ToolUseId,
                    ToolName = toolUse.ToolName,
                    InputJson = toolUse.InputJson,
                };
                Transcript.Add(toolUseRow);

                // AC-532: this top-level call is now outstanding — reuses the row's own ToolHeader ("Bash  ·
                // dotnet build") rather than re-deriving a summary from the input JSON a second time.
                _activeToolCalls.Add(new ActiveToolCall(toolUse.ToolUseId, toolUseRow.ToolHeader, DateTimeOffset.Now));
                _RaiseActiveToolActivityChanged();

                // The wait starts here, so the lead-in is spoken here. Until now the only mid-turn flushes were a
                // permission prompt and a question, which was enough while every tool call raised one — and stopped
                // being enough the moment an operator turned on bypassPermissions or the cockpit's consent bypass
                // (AC-575). Then nothing paused the turn, nothing flushed, and a spoken assistant went silent from
                // the question until the whole answer was ready. Flushing on the call itself does not depend on
                // anyone being asked anything.
                _FlushPendingProseForReadAloud();

                // AC-597: and when there was no lead-in to flush, say one of our own. Two turns in five reach this
                // line with nothing written yet, and silence from the question to the answer reads as unheard.
                _SpeakLeadInIfTheModelGaveNone();

                // AC-598: the wait starts here too, so a turn that said its lead-in and then spends two minutes in
                // tools still gives a sign of life.
                _RestartSignOfLifeClock();
                break;

            case ToolResult toolResult:
                // AC-146: a sub-agent's own tool result couples to its tool-use row inside that lane's nested
                // rows, the same by-tool_use_id matching the top-level branch below uses — never the flat
                // Transcript, and never the output/tool-activity signals a top-level result raises.
                if (_ResolveSubAgentLane(toolResult.ParentToolUseId) is { } toolResultLane)
                {
                    var nestedToolUseEntry = toolResultLane.Anchor.SubAgentRows.LastOrDefault(
                        t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == toolResult.ToolUseId);
                    if (nestedToolUseEntry is not null)
                    {
                        nestedToolUseEntry.SetResult(toolResult.Content, toolResult.IsError);
                    }
                    else
                    {
                        toolResultLane.Anchor.SubAgentRows.Add(new TranscriptEntryViewModel(
                            TranscriptEntryKind.ToolResult,
                            toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                    }

                    break;
                }

                var toolUseEntry = Transcript.LastOrDefault(
                    t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == toolResult.ToolUseId);
                if (toolUseEntry is not null)
                {
                    // Couple the result to its tool-call row (L14) so it renders as an expandable
                    // section beneath that call, instead of a detached row that loses which call it
                    // belongs to — the pain with parallel tool calls.
                    toolUseEntry.SetResult(toolResult.Content, toolResult.IsError);
                }
                else
                {
                    // No matching tool-use in view (e.g. a result arriving first): fall back to a row.
                    Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.ToolResult,
                        toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                }

                // AC-532: this call is no longer outstanding, whichever way it resolved — success, error, or a
                // permission denial the driver reported as a tool result.
                var activeCallIndex = _activeToolCalls.FindIndex(call => call.ToolUseId == toolResult.ToolUseId);
                if (activeCallIndex >= 0)
                {
                    _activeToolCalls.RemoveAt(activeCallIndex);
                    _RaiseActiveToolActivityChanged();
                }

                // AC-146: a result naming a parent this pane never resolved to a lane (the anchor tool-use row
                // was never seen) is coupled/shown above like any other, so nothing vanishes silently — but it is
                // an orphaned sub-agent event, not a genuine top-level one, so it must not be mistaken for the
                // session's own output: neither signal below is for anything but the top-level conversation.
                if (!string.IsNullOrEmpty(toolResult.ParentToolUseId))
                {
                    break;
                }

                // Tool output is where a shelled-out `gh pr create`/`git push` prints its pull-request url, so
                // it is the primary channel the PR watcher scans (the read/observe surface).
                RaiseOutputText(toolResult.Content);

                // And, coupled with its call, the structured tool-activity signal (AC-116): the tool-use row we
                // just found carries the name and input, the result carries the content — together they let a
                // plugin react to a specific tool completing (a YouTrack tracker attaching this turn's images to
                // an issue the agent created) rather than pattern-matching prose. Only raised when the matching
                // tool-use is in view, so the name is known.
                if (toolUseEntry is { ToolName: { } toolName })
                {
                    RaiseToolActivity(toolName, toolUseEntry.InputJson ?? "{}", toolResult.Content, toolResult.IsError);
                }

                break;

            case PermissionRequested permission:
                // AC-146: a sub-agent's own tool call can need approval too — the row it responds to lives
                // nested under its Task anchor rather than in the flat Transcript, so look there when the
                // top-level search comes up empty.
                var entry = Transcript.LastOrDefault(t => t.ToolUseId == permission.ToolUseId)
                    ?? _ResolveSubAgentLane(permission.ParentToolUseId)?.Anchor.SubAgentRows.LastOrDefault(t => t.ToolUseId == permission.ToolUseId);

                // A pre-authorized tool for a self-driving run (AC-215): auto-allow it here rather than raising a prompt
                // the autonomous run has no one to answer — that stall left the run stuck first on its own
                // autopilot_step_done, then on the Bash its work needs. Either the named control-tool set (the narrow
                // default) or the "worktree is the boundary" stance (Raymond 2026-07-23), where an autonomous run
                // isolated in a throwaway worktree auto-allows every tool so it can actually run its work — the run's
                // isolation is the containment, not the per-call gate. Sends the same allow the Allow button does, but
                // fire-and-forget: Apply is a synchronous event handler, so it cannot await the driver call the way the
                // command can (a driver fault here is rare and would surface as the run stalling on this one permission).
                if ((_preApproveAllTools || _preApprovedTools.Contains(permission.ToolName)) && _runtime is not null)
                {
                    if (entry is not null)
                    {
                        entry.PermissionDecision = "Allowed";
                        entry.IsPendingPermission = false;
                    }

                    _ = _runtime.RespondToPermissionAsync(permission.ToolUseId, allow: true);
                    break;
                }

                if (entry is not null)
                {
                    entry.IsPendingPermission = true;
                    // AC-532: a top-level call stalling on this prompt is why the turn looks idle right now —
                    // flip the composer's activity band from "running" to "waiting for permission" so that reads
                    // as waiting on the operator rather than as the tool quietly still working.
                    _RaiseActiveToolActivityChanged();
                }

                _needsAttention = true;
                // Speak the lead-in the reply gave before this tool needs approval, rather than holding it back
                // until the operator answers the prompt (AC-97).
                _FlushPendingProseForReadAloud();
                _RecomputeStatus();
                break;

            case Question question:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question.Text));
                // Same as a permission prompt: a question pauses the turn, so speak what was said before it now.
                _FlushPendingProseForReadAloud();
                break;

            case TurnCompleted turn:
                // Only surface a turn row when it failed — a plain "Turn completed (success)" row is
                // noise in the transcript (T4). The Done status still fires below.
                if (turn.IsError)
                {
                    Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.TurnCompleted, $"Turn failed ({turn.Subtype})"));
                }

                // AC-410: the first turn of a restored pane's own launch settles the resume snapshot, one way or
                // the other. A failure here is a resume that was actually tried and refused (an expired
                // conversation id makes claude --resume print "No conversation found" and end the turn as
                // error_during_execution with no Result) — the offer comes back with that reason instead of
                // leaving the operator looking at a silently failed session with no banner explaining why.
                if (_restoredOfferSnapshot is { } restoredOffer)
                {
                    _restoredOfferSnapshot = null;
                    if (turn.IsError)
                    {
                        RestoreOffer = restoredOffer with
                        {
                            Availability = SessionRestoreAvailability.Gone,
                            Explanation = _DegradedTurnExplanation(turn),
                        };
                    }
                }

                _FlushPendingProseForReadAloud();

                _currentTurnAssistantEntries.Clear();
                _readAloudFlushedLength = 0;
                _spokenSomethingThisTurn = false;
                _StopSignOfLifeClock();
                _currentAssistantEntry = null;
                _CloseThinkingRow();
                // A sub-agent does not outlive the turn that spawned it (AC-146): a fresh Task call next turn
                // gets a fresh anchor and lane, never one still holding a finished sub-agent's dangling state.
                _subAgentLanes.Clear();
                _currentOrphanedSubAgentTextEntry = null;
                // This turn's images belong to this turn only (AC-116): drop them so a later image-less turn's
                // tool call attaches nothing stale.
                ClearCurrentTurnImages();
                // AC-532 safety net: every turn ends here or in SessionError below, whether or not each of its
                // tool calls got a matching ToolResult first (an interrupt ends the turn without one) — clearing
                // unconditionally is what keeps the activity band from surviving into a turn that is not running
                // anymore, the "stuck showing busy" failure mode a plain per-ToolResult clear cannot cover.
                if (_activeToolCalls.Count > 0)
                {
                    _activeToolCalls.Clear();
                    _RaiseActiveToolActivityChanged();
                }

                // AC-531: deliberately no _backgroundTasks/_RebuildBackgroundTaskRows() call here, unlike
                // _activeToolCalls just above. A sub-agent or shell does not end just because this turn did — that
                // is the whole reason SessionStatus.WorkingBackground exists — so clearing it on TurnCompleted
                // would reopen the exact gap this ticket closed: the composer's tool-activity band and
                // "Thinking…" both go quiet here, and the background-work button is what still tells the operator
                // something is running. It only ever changes on its own BackgroundTasksChanged event.
                _hasCompletedATurn = true;
                IsBusy = false;
                _AccumulateUsage(turn);
                _RefreshLimits();
                _RecomputeStatus();
                // "exit" turn finished → ask the cockpit to close this session (T10). Skip draining the
                // queue: the session is going away, so anything still queued is moot.
                if (_closeAfterTurn)
                {
                    _closeAfterTurn = false;
                    RaiseCloseRequested();
                    break;
                }

                // A completed turn (success or error result) frees the session, so send the next queued
                // message (T8). A SessionError event does not drain the queue — the chips stay so a
                // broken session isn't cascaded through every queued message.
                _TryDispatchNextQueued();
                break;

            case SessionError error:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, error.Message));
                // A session error ends the turn without a TurnCompleted, so drop this turn's images here too —
                // otherwise a later image-less turn's tool call could attach the errored turn's stale images (AC-116).
                ClearCurrentTurnImages();
                IsBusy = false;
                // Whatever was outstanding died with the session (AC-276). Unlike the TTY route this one has no
                // safety timeout to fall back on, so a sub-agent left in the list here would hold a crashed session
                // on WorkingBackground forever — and make closing it ask "still working?" on the way out.
                _backgroundTasks = [];
                OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
                _RebuildBackgroundTaskRows();
                // AC-532: same reasoning as the background-task list above — a crashed driver never sends the
                // ToolResult that would otherwise have cleared this, so the composer must not go on showing a
                // tool as running past the session that was running it.
                if (_activeToolCalls.Count > 0)
                {
                    _activeToolCalls.Clear();
                    _RaiseActiveToolActivityChanged();
                }

                _RecomputeStatus();
                break;

            // The driver restated what is still outstanding (AC-276). Kept verbatim — the weighing of sub-agent
            // versus shell belongs to _RecomputeStatus and the notification gate, not here, so this stays a
            // straight assignment even when the list is empty (which is how the last task ending arrives).
            case BackgroundTasksChanged backgroundTasks:
                _backgroundTasks = backgroundTasks.Tasks;
                OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
                _RebuildBackgroundTaskRows();
                _RecomputeStatus();
                break;

            case SessionStatusChanged statusChanged:
                // needs_action non-empty is the CLI telling the host the session wants attention
                // (e.g. a pending question) — same "jump out in the sidebar" signal as a pending
                // tool permission. RateLimitInfo/UnknownEvent stay out of scope for status (a
                // per-session status overview is a later increment; sub-agent nesting shipped in
                // AC-146 via ParentToolUseId, above); ConsumeEventsAsync
                // already delivers them to any future subscriber.
                if (!string.IsNullOrEmpty(statusChanged.NeedsAction))
                {
                    _needsAttention = true;
                }

                _RecomputeStatus();
                break;

            // Reasoning/thinking deltas stream into their own dimmed, collapsible row (AC-213, revising AC-144).
            // The row is added at every reading level but only *renders* at Developer — its IsRowVisible gates it
            // off at Focus/Simple, which stay calm (AC-138). Contiguous deltas of the same provider block append
            // onto one row (like assistant prose); a new block index starts a fresh row. The "Thinking…" indicator
            // is left untouched (thinking is deliberately absent from the clear-set above), so the pulse still
            // signals the model is working and this row does not double it. Empty deltas (a bare block_start) add
            // nothing.
            case AssistantThinkingDelta thinkingDelta:
                if (!string.IsNullOrEmpty(thinkingDelta.Thinking))
                {
                    // AC-146: a sub-agent's own reasoning stays in its lane, same rule as its text above.
                    if (_ResolveSubAgentLane(thinkingDelta.ParentToolUseId) is { } thinkingLane)
                    {
                        if (thinkingLane.CurrentThinkingEntry is null || thinkingDelta.BlockIndex != thinkingLane.CurrentThinkingBlockIndex)
                        {
                            thinkingLane.CurrentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty)
                            {
                                IsExpanded = true,
                            };
                            thinkingLane.CurrentThinkingBlockIndex = thinkingDelta.BlockIndex;
                            thinkingLane.Anchor.SubAgentRows.Add(thinkingLane.CurrentThinkingEntry);
                        }

                        thinkingLane.CurrentThinkingEntry.AppendText(thinkingDelta.Thinking);
                        break;
                    }

                    if (_currentThinkingEntry is null || thinkingDelta.BlockIndex != _currentThinkingBlockIndex)
                    {
                        _currentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty)
                        {
                            IsExpanded = true,
                        };
                        _currentThinkingBlockIndex = thinkingDelta.BlockIndex;
                        Transcript.Add(_currentThinkingEntry);
                    }

                    _currentThinkingEntry.AppendText(thinkingDelta.Thinking);
                }

                break;

            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
    }

    // Ends the currently-streaming reasoning row (AC-213) so the next thinking block, or the next turn, opens a
    // fresh row instead of appending onto a stale one. Called wherever the assistant text row is likewise reset.
    private void _CloseThinkingRow()
    {
        _currentThinkingEntry = null;
        _currentThinkingBlockIndex = -1;
    }

    /// <summary>
    /// Derives <see cref="SessionStatus"/> from the flags this view model already tracks:
    /// busy while a turn is in flight, needs-attention while a permission/needs_action signal is
    /// outstanding (takes priority over busy so it still surfaces if a new send arrives before the
    /// user reacts), done once a turn completed and nothing is pending, idle otherwise.
    /// <para>
    /// A finished turn is not the same as a finished session (AC-276). The main agent legitimately reaches
    /// <c>end_turn</c> several times per instruction while sub-agents it spawned keep running — measured at 1195
    /// of 3054 turn endings across 77 real sessions — so <see cref="IsBusy"/> alone flips to Done and back on
    /// every one of them. A still-running sub-agent therefore holds the session on
    /// <see cref="SessionStatus.WorkingBackground"/>, which is what makes that value reachable on the SDK route
    /// at all.
    /// </para>
    /// <para>
    /// A shell deliberately does <em>not</em>: it may be a dev server or a <c>tail -f</c> that never ends, and
    /// pinning the status on that would be worse than the premature Done it set out to fix. It is held back at the
    /// notification instead — see <see cref="HasOutstandingBackgroundShells"/>.
    /// </para>
    /// </summary>
    private void _RecomputeStatus()
    {
        SessionStatus = (_needsAttention, IsBusy, _HasOutstandingSubAgents) switch
        {
            (true, _, _) => SessionStatus.NeedsAttention,
            (false, true, _) => SessionStatus.Busy,
            (false, false, true) => SessionStatus.WorkingBackground,
            (false, false, false) => _hasCompletedATurn ? SessionStatus.Done : SessionStatus.Idle,
        };
    }

    /// <summary>
    /// The work outliving the current turn, as the driver last reported it. Replaced wholesale rather than
    /// added to and removed from: the event carries the complete set every time (see
    /// <see cref="BackgroundTasksChanged"/>), so a dropped event costs one stale reading instead of permanently
    /// desynchronising a ledger.
    /// </summary>
    private IReadOnlyList<BackgroundTask> _backgroundTasks = [];

    private bool _HasOutstandingSubAgents => _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.SubAgent);

    /// <summary>
    /// True while a backgrounded shell is still running (AC-276). It does not hold the status — a never-ending
    /// dev server would pin the session forever — but it does suppress the "session finished" notification, which
    /// would otherwise announce a session that is still doing something.
    /// </summary>
    public override bool HasOutstandingBackgroundShells => _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.Shell);

    // Give this turn's reported usage and cost to the session meter (#8) and refresh the bound meter text.
    // The meter sums the tokens and follows the cost, which the result reports as a session total rather
    // than a per-turn share. A turn whose result carried neither (e.g. an error) leaves the totals where
    // they were but is still counted as a turn.
    internal void _AccumulateUsage(TurnCompleted turn)
    {
        _usage.Add(turn.Usage, turn.TotalCostUsd);
        HasUsage = _usage.HasData;
        UsageSummary = _usage.Summary;
        UsageTooltip = _usage.Tooltip;
        _RecordUsageSnapshot();
    }

    // Write the running totals to the usage trail after every turn (AC-251), so they outlive the session and the
    // app — recording only at the end would lose exactly the run that crashed, which is the case worth measuring.
    // Not awaited here — a turn settling must not wait on a file, and the trail swallows its own failures by
    // contract — but kept so teardown can wait for it; without that the last turn races the process out.
    // A turn that reported nothing (an error result) leaves no record rather than a row of zeroes.
    private void _RecordUsageSnapshot()
    {
        if (_usageHistory is null || !_usage.HasData)
        {
            return;
        }

        _pendingUsageWrite = _usageHistory.RecordAsync(new UsageSnapshot
        {
            PaneId = PaneId,
            StartedAt = _startedAt,
            RecordedAt = DateTimeOffset.Now,
            RunKind = RunKind,
            RunId = RunId,
            RunLabel = RunLabel,
            ProfileLabel = ActiveProfileLabel,
            Model = SelectedModel.Value,
            InputTokens = _usage.InputTokens,
            OutputTokens = _usage.OutputTokens,
            CacheReadInputTokens = _usage.CacheReadInputTokens,
            CacheCreationInputTokens = _usage.CacheCreationInputTokens,
            TotalCostUsd = _usage.TotalCostUsd,
            Turns = _usage.Turns,
        });
    }

    /// <inheritdoc/>
    public override async Task<bool> SendPromptAsync(string prompt)
    {
        // Running, not merely present. A runtime whose driver never came up is still held by the pane, and it accepts
        // a send and hands back a completed task with nothing having gone anywhere — so the old "is there a runtime"
        // check reported a turn that never happened, and, once this method began marking turns in flight, marked one
        // that nothing would ever finish: no driver means no event pump, and TurnCompleted and SessionError are the
        // only two things that clear the flag. The pane would have read as working for the rest of its life, queueing
        // every later message behind a turn that was never there.
        if (_runtime is not { IsRunning: true } runtime)
        {
            return false;
        }

        // A turn started from here is as real as one the operator typed, and the rest of the cockpit only learns that
        // from these flags: the composer queues behind IsBusy rather than sending on top of a running turn, and
        // AC-395's wake refuses a pane that is already working. Marked here as well as in _DispatchMessageAsync
        // because a turn nobody marked busy is one the session goes on reporting itself idle through — and the next
        // urgent message, or the operator's own send, then lands on top of it.
        //
        // Set before the first await on purpose. Both callers reach this from the UI thread, so the flag is up before
        // control returns to whoever asked for the turn; a second wake arriving in that same moment sees Busy rather
        // than the state from before this one started.
        IsBusy = true;
        _RecomputeStatus();

        try
        {
            // Through the same funnel as the composer's own sends: a scheduled resume is a real turn on a real session,
            // so mail waiting for this pane belongs on it just as much. Routing it around the funnel is exactly the kind
            // of second path that leaves one route delivering and the other not.
            await _SendWithWaitingMessagesAsync(runtime, prompt, images: null);
        }
        catch
        {
            // The turn never left, so the session is not working — left standing, it would read as permanently busy:
            // the composer would queue forever and no later message could ever wake it. Rethrown rather than swallowed,
            // because the callers already decide what a failed prompt means for them.
            IsBusy = false;
            _RecomputeStatus();
            throw;
        }

        return true;
    }

    /// <summary>What a restored pane's degraded offer shows for why (AC-410) — the provider's own <c>errors[]</c> when it reported one, else the bare subtype, since <see cref="TurnCompleted.Result"/> is exactly what an error_during_execution turn does not carry.</summary>
    private static string _DegradedTurnExplanation(TurnCompleted turn) =>
        turn.Errors is { Count: > 0 } errors
            ? string.Join('\n', errors)
            : $"Claude could not resume the earlier conversation ({turn.Subtype}).";

    // Pulls the driver's latest limits into the header bars. Read at each turn boundary rather than on a timer:
    // the provider reports how full the context window is when a turn ends, so that is when the numbers change —
    // and a session with no limits feed simply reads null and keeps the bars hidden.
    private void _RefreshLimits()
    {
        if (_runtime?.CurrentStatus is { HasAny: true } status)
        {
            ContextUsedPercent = status.ContextUsedPercent;
            RateLimits.Clear();
            foreach (var window in status.RateLimits)
            {
                RateLimits.Add(window);
            }

            LimitsTooltip = status.Describe();
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        // Before anything else: let the last turn's usage write land. It was left unawaited so the turn could
        // settle without waiting on a file, and a session closing right behind it would otherwise take the
        // process down before the record reached disk (AC-251). The trail swallows its own failures, so this
        // waits on a task that does not fault.
        if (_pendingUsageWrite is { } pendingUsageWrite)
        {
            await pendingUsageWrite;
        }

        await _StopRuntimeAsync();
    }

    // Ends this panel's runtime and detaches from it, leaving the panel itself intact. Two callers: the panel
    // going away (DisposeCoreAsync) and a context clear that starts a new conversation in the same pane (AC-564).
    //
    // Stop through the manager, which owns the runtime: the same path an orchestrator's stop_task (#67) takes,
    // so a session ends in one state however it was ended. Unsubscribing first means the teardown cannot post
    // another event at a panel that is no longer listening — and since the pump no longer marshals to the UI
    // thread, killing the child no longer depends on the dispatcher still being alive, which is what used to
    // hang shutdown with a live child claude (#32).
    private async Task _StopRuntimeAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        _runtime.EventAppended -= _OnSessionEvent;
        var runtime = _runtime;
        _runtime = null;
        OnPropertyChanged(nameof(IsSessionReady));

        if (_sessionManager is not null)
        {
            await _sessionManager.StopAsync(runtime.Id);
        }
        else
        {
            await runtime.DisposeAsync();
        }
    }
}
