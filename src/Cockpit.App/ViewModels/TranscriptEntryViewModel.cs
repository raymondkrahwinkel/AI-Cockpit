using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// Kind of a single line rendered in the Claude session transcript view.
public enum TranscriptEntryKind
{
    AssistantText,
    UserText,
    ToolUse,
    ToolResult,
    Question,
    TurnCompleted,
    Error,

    // A streamed reasoning/extended-thinking block (AC-213). Rendered as a dimmed, collapsible section, and
    // only at the Developer reading level — `TranscriptEntryViewModel.IsRowVisible` keeps it hidden
    // at Focus/Simple, which stay calm (AC-138), restoring thinking that AC-144 had dropped app-wide.
    Thinking,

    // AC-564 marks where agent memory stopped while keeping the full transcript as the pane's audit surface.
    Divider,
}

// A single row in the transcript view. Assistant text entries are mutated in place
// (`AppendText`) so streaming deltas render as growing text rather than
// as new rows.
public partial class TranscriptEntryViewModel : ViewModelBase
{
    // AC-1090: what the transcript log keys a row's versions on, and restored from it. A GUID rather than the
    // row's index: the transcript is not append-only in memory — a reset clears it — and a shifted index would
    // silently rewrite the wrong row.
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    public TranscriptEntryKind Kind { get; }

    public bool IsToolResult => Kind == TranscriptEntryKind.ToolResult;

    public bool IsToolUse => Kind == TranscriptEntryKind.ToolUse;

    // A streamed reasoning/extended-thinking row (AC-213), rendered as its own dimmed, collapsible section.
    public bool IsThinking => Kind == TranscriptEntryKind.Thinking;

    // A rule across the transcript with its label in the middle (AC-564) — not text in the reply column.
    public bool IsDivider => Kind == TranscriptEntryKind.Divider;

    // Rows not rendered as a tool-use, a standalone tool result, a thinking section or a divider — assistant/user text, questions, errors.
    public bool IsPlainText => !IsToolResult && !IsToolUse && !IsThinking && !IsDivider;

    // Assistant prose renders as markdown (T9).
    public bool IsAssistantMarkdown => Kind == TranscriptEntryKind.AssistantText;

    // The user's own message, rendered as a right-aligned bubble (T2) — plain text, not markdown.
    public bool IsUserRow => Kind == TranscriptEntryKind.UserText;

    // A driver-reported failure (AC-720), rendered as a severity-coloured card rather than plain text.
    public bool IsErrorRow => Kind == TranscriptEntryKind.Error;

    // Set on a TurnCompleted row that is an actual failure (AC-728), so it renders through the same card as a
    // driver-reported SessionError instead of growing its own. A non-failure TurnCompleted row (e.g. "Signing
    // in again…") leaves this false and stays plain text.
    public bool IsFailedTurnRow { get; init; }

    // Whether this row renders as the accent-bordered severity card (AC-720) instead of plain wrapped text.
    public bool ShowsFailureCard => IsErrorRow || IsFailedTurnRow;

    // Plain rows that are neither the user bubble nor markdown: questions and turn results. An error or
    // failed-turn row is plain text too, but gets its own severity-coloured card instead of this branch.
    // A broker question (AC-955) renders through ToolBranch's card instead, so it doesn't count as plain text.
    public bool IsPlainNonMarkdown => IsPlainText && !IsAssistantMarkdown && !IsUserRow && !ShowsFailureCard && !HasQuestionPrompts;

    // Assistant prose, questions/errors and turn results carry a top timestamp, except a reply continuation:
    // it belongs to the same answer, so repeating its time would make one answer read as separate messages. User
    // and tool rows carry their timestamp inline instead (AC-144); thinking rows have none.
    public bool IsTopTimestampRow => !IsReplyContinuation && !IsUserRow && !IsToolUse && !IsThinking;

    // Deferred branches return only the matching immutable kind, avoiding Avalonia construction of hidden siblings.
    // AC-715/AC-955 share QuestionBranch for permission and broker questions; mutable branches raise their own flags.
    public object? ToolBranch => IsToolUse || HasQuestionPrompts ? this : null;
    public object? UserBranch => IsUserRow ? this : null;
    // AC-1238: a streamed reply arrives as one row per finished markdown block, because a row that keeps growing
    // while the virtualising panel has it realised is what makes that panel lose its own anchor. The group still
    // reads as one reply: the badge and the name sit on the first row, the actions on the last.
    [ObservableProperty]
    private bool _isReplyContinuation;

    [ObservableProperty]
    private bool _isReplyTail = true;

    // The badge keeps its box on a continuation row — hiding it outright would step the prose beside it left
    // halfway through an answer.
    public double ReplyBadgeOpacity => IsReplyContinuation ? 0 : 1;

    partial void OnIsReplyContinuationChanged(bool value) => OnPropertyChanged(nameof(ReplyBadgeOpacity));

    // Every row of the reply this row belongs to, in order. Null for a row that was never part of a split.
    internal IReadOnlyList<TranscriptEntryViewModel>? ReplyRows { get; set; }

    // What "copy this reply" hands over: the whole reply, not the block the button happens to sit under.
    public string ReplyTextWithImageSuffix => ReplyRows is null
        ? TextWithImageSuffix
        : string.Concat(ReplyRows.Select(row => row.TextWithImageSuffix));

    public object? AssistantBranch => IsAssistantMarkdown ? this : null;
    public object? ThinkingBranch => IsThinking ? this : null;
    public object? DividerBranch => IsDivider ? this : null;
    public object? FailureBranch => ShowsFailureCard ? this : null;
    public object? QuestionBranch => HasQuestionPrompts ? this : null;
    public object? ToolBlockBranch => ShowToolBlock ? this : null;

    // Chevron icon for a row's expand/collapse toggle, shared by the tool-use header and the standalone tool-result row.
    public MaterialIconKind ToggleIconKind => IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    // Label for a standalone (orphan) tool-result row's toggle; the chevron itself renders separately as `ToggleIconKind`.
    public string ToggleLabel => IsExpanded ? "Tool result" : "Tool result (click to show)";

    public string ToolResultPrefix => IsResultError ? "Tool error:" : "Tool result:";

    // Compact one-line label for a collapsed tool-use row (T5): tool name + a short hint pulled from the
    // input (command/file/pattern/…), so a call reads as "Bash · dotnet build" instead of the full input
    // JSON. The full input shows once expanded; the row's own chevron renders separately as `ToggleIconKind`.
    public string ToolHeader
    {
        get
        {
            var name = string.IsNullOrEmpty(ToolName) ? "Tool" : ToolName;
            var summary = _ToolSummary(InputJson);
            return summary.Length == 0 ? name : $"{name}  ·  {summary}";
        }
    }

    // --- Background tool calls (AC-1056) ----------------------------------------------------------------------

    // True for a call that runs outside the turn: `Bash` asked for it up front with `run_in_background`, or an
    // MCP tool was moved there once it overran. Both name a task id in their result, which is the same id the
    // session's `BackgroundTasksChanged` ledger reports on — so the badge can say "still running", not just "was".
    public bool IsBackgroundTool => _RequestsBackground(InputJson) || BackgroundTaskId is not null;

    // The provider's id for this call's background task, read back out of the result line announcing the
    // hand-off. Null until that result arrives, and on every ordinary call. AC-1090 restores it directly rather
    // than re-deriving it in `SetResult`: the recorded id outlives a result no longer held in full.
    public string? BackgroundTaskId { get; internal set; }

    // Set by the owning session from its latest background-task snapshot: true while this row's task is still in it.
    [ObservableProperty]
    private bool _isBackgroundTaskLive;

    // Set once a `BackgroundTaskNotification` (AC-1057) names this row's task, overriding the inference below with
    // the provider's own verdict. Null until then; `Unknown` (an unrecognised status) falls back to that same
    // inference rather than guessing completed.
    [ObservableProperty]
    private BackgroundTaskStatus? _backgroundNotificationStatus;

    // The provider's own verdict once it arrives; until then, inferred: running until the ledger stops reporting
    // the task, failed the moment the call itself came back an error.
    public string BackgroundStatusText => BackgroundNotificationStatus switch
    {
        BackgroundTaskStatus.Completed => "Background · done",
        BackgroundTaskStatus.Failed => "Background · failed",
        _ => IsResultError
            ? "Background · failed"
            : IsBackgroundTaskLive || !HasResult ? "Background · running" : "Background · done",
    };

    // The tool result coupled to this tool-use row by tool_use_id (L14), or null until it arrives.
    [ObservableProperty]
    private string? _resultText;

    // True when the coupled tool result reported an error.
    [ObservableProperty]
    private bool _isResultError;

    // True once a result has been coupled to this tool-use row, driving its expandable result section.
    public bool HasResult => ResultText is not null;

    // The result as it should be shown (T6): JSON is pretty-printed for readability, everything else
    // is passed through unchanged. Kept separate from the raw `ResultText` so the copy
    // button hands the operator the same formatted text they see.
    public string ResultDisplayText => _FormatResult(ResultText);

    // True when the result reads as structured/code (JSON, multi-line, or long) and so should render
    // in a monospace code box with a copy button rather than as a wrapped paragraph (T6).
    public bool ResultIsCodeLike
    {
        get
        {
            var text = ResultText;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[') || text.Contains('\n') || text.Length > 200;
        }
    }

    [ObservableProperty]
    private string _text;

    // Collapsed by default for tool-use and standalone tool-result rows, so their input/output stays folded until the operator expands the chip.
    [ObservableProperty]
    private bool _isExpanded;

    // Non-null only for `TranscriptEntryKind.ToolUse` rows awaiting an allow/deny decision.
    [ObservableProperty]
    private bool _isPendingPermission;

    [ObservableProperty]
    private string? _permissionDecision;

    public string? ToolUseId { get; init; }

    // --- Reply relation (AC-935) --------------------------------------------------------------------------------
    // An object reference to the target row, which is the stable key while the app runs. AC-1090's log, which
    // outlives the run, records `Id` on both ends instead and resolves the references back on restore.

    // The row this reply answers, set once at construction — null for an ordinary (non-reply) message.
    public TranscriptEntryViewModel? ReplyTo { get; init; }

    public bool HasReplyTo => ReplyTo is not null;

    // A one-line citation of the target's own text (SessionViewModel builds the wire-format prefix from the
    // same helper), shown above this reply so both the operator and, via the wire text, the model can tell
    // which message it answers.
    public string ReplyExcerpt => ReplyTo is null ? string.Empty : BuildReplyExcerpt(ReplyTo.TextWithImageSuffix);

    // The most recent reply that targeted this row, so its "answered" marker can jump straight to it. Not a
    // list: nothing here needs more than "was this answered, and where is the latest answer".
    [ObservableProperty]
    private TranscriptEntryViewModel? _latestReply;

    public bool HasReplies => LatestReply is not null;

    partial void OnLatestReplyChanged(TranscriptEntryViewModel? value) => OnPropertyChanged(nameof(HasReplies));

    // Collapsed to one line and capped so a long status report does not double a reply's token cost for no
    // identification benefit; quotes are swapped so they never close the wire format's own quoting
    // (`[reply to "<excerpt>"]: <input>`).
    public static string BuildReplyExcerpt(string text)
    {
        var oneLine = string.Join(' ', text.Split(ReplyExcerptSplitChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace('"', '\'');
        return oneLine.Length > ReplyExcerptMaxLength ? oneLine[..ReplyExcerptMaxLength] + "…" : oneLine;
    }

    private const int ReplyExcerptMaxLength = 200;

    private static readonly char[] ReplyExcerptSplitChars = [' ', '\t', '\r', '\n'];

    // AC-778 keeps this row's images for the running session so its [+N image] fragment can reopen them.
    // Null means no image or unavailable wiring; transcript persistence cannot replay those bytes.

    public IReadOnlyList<ImageAttachment>? Images { get; init; }

    public bool HasImages => Images is { Count: > 0 };

    public string ImageChipLabel => ImageCountLabel.Format(Images?.Count ?? 0);

    // `Text` plus the chip's own label, for callers that read a row's content as one string rather than through
    // the chip (copy-to-clipboard, session-watch pattern matching, the assistant's read-transcript MCP surface)
    // — the same fragment `Text` itself used to carry before the chip took over rendering it.
    public string TextWithImageSuffix => !HasImages
        ? Text
        : string.IsNullOrEmpty(Text) ? ImageChipLabel : $"{Text}  {ImageChipLabel}";

    // --- Clarifying questions (AC-715) ------------------------------------------------------------------------
    // An AskUserQuestion rides the permission callback like a tool approval, but Allow/Deny is not an answer to it:
    // a row carrying parsed questions renders them as its own card, and keeps them there once answered.

    [ObservableProperty]
    private IReadOnlyList<AskUserQuestionViewModel>? _questionPrompts;

    public bool HasQuestionPrompts => QuestionPrompts is { Count: > 0 };

    // The generic allow/deny row: every pending consent except a question, which has its own Send.
    public bool IsPendingToolPermission => IsPendingPermission && !HasQuestionPrompts;

    // AC-722/#614 creates consent buttons only while pending: command subscriptions otherwise pin recycled rows.
    // Clearing this removes the bindings/subscriptions; resolved and non-pending rows never create the buttons.
    public object? PermissionBranch => IsPendingToolPermission ? this : null;

    // Set only on a card the assistant's own broker raised (AC-955's `ask_structured_question`), which has no
    // permission callback to ride: this is that card's own "still open for an answer", parallel to
    // `IsPendingPermission` for the AC-715 kind, and cleared the same way once Send goes out.
    [ObservableProperty]
    private bool _isPendingBrokerAnswer;

    // A question card is still open for an answer whichever route raised it — the permission callback (AC-715)
    // or the assistant's own broker (AC-955). Send and CanSubmitAnswers do not care which.
    public bool IsAwaitingAnswer => IsPendingPermission || IsPendingBrokerAnswer;

    // Send only lights up once every question on the card has an answer — a half-filled card would send the agent
    // an answers object missing the keys it asked about.
    public bool CanSubmitAnswers =>
        IsAwaitingAnswer && QuestionPrompts is { Count: > 0 } prompts && prompts.All(prompt => prompt.HasAnswer);

    partial void OnIsPendingBrokerAnswerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAwaitingAnswer));
        OnPropertyChanged(nameof(CanSubmitAnswers));
    }

    // --- Error severity (AC-720) ------------------------------------------------------------------------------
    // The driver's own Kind, or the host's text-heuristic guess when it hasn't set one — presentation only,
    // never behaviour (SessionViewModel resolves which of the two this is before setting it).

    [ObservableProperty]
    private SessionErrorKind _errorKind = SessionErrorKind.Unknown;

    [ObservableProperty]
    private DateTimeOffset? _retryAfter;

    // Auth/config problems block the session until the operator acts — the row that gets the "Log in…" action.
    // AC-939: keyed on ShowsFailureCard (not IsErrorRow) so a failed-turn row (AC-728) can render blocking/temporary
    // too, now that SessionViewModel classifies its ErrorKind instead of leaving it permanently Unknown.
    public bool IsBlockingError => ShowsFailureCard && ErrorKind == SessionErrorKind.AuthRequired;

    // Rate limits and outages resolve on their own; the operator's own next attempt is the only "action".
    public bool IsTemporaryError => ShowsFailureCard && ErrorKind is SessionErrorKind.RateLimited or SessionErrorKind.ServiceUnavailable;

    // Everything else — a parse failure, an empty reply, an unclassified driver, or a failed turn (AC-728,
    // which never carries a SessionErrorKind at all) — and always the safe default: never guessed red or
    // amber (AC-720 acceptance criterion).
    public bool IsInformationalError => ShowsFailureCard && !IsBlockingError && !IsTemporaryError;

    public MaterialIconKind ErrorIconKind => ErrorKind switch
    {
        SessionErrorKind.AuthRequired => MaterialIconKind.LockAlertOutline,
        SessionErrorKind.RateLimited => MaterialIconKind.GaugeFull,
        SessionErrorKind.ServiceUnavailable => MaterialIconKind.CloudOffOutline,
        _ => MaterialIconKind.InformationCircleOutline,
    };

    public bool HasRetryAfter => RetryAfter is not null;

    public string RetryAfterText => RetryAfter is { } retryAfter ? $"Try again after {retryAfter.ToLocalTime():HH:mm}" : string.Empty;

    partial void OnErrorKindChanged(SessionErrorKind value)
    {
        OnPropertyChanged(nameof(IsBlockingError));
        OnPropertyChanged(nameof(IsTemporaryError));
        OnPropertyChanged(nameof(IsInformationalError));
        OnPropertyChanged(nameof(ErrorIconKind));
    }

    partial void OnRetryAfterChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(HasRetryAfter));
        OnPropertyChanged(nameof(RetryAfterText));
    }

    // --- Row action (AC-715) ----------------------------------------------------------------------------------
    // One optional affordance any row can carry. Deliberately not tied to questions: AC-713's "Login" on an
    // auth-error row is the next caller, and without this each such row would grow its own bespoke card.

    [ObservableProperty]
    private string? _actionLabel;

    [ObservableProperty]
    private IRelayCommand? _actionCommand;

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel) && ActionCommand is not null && LoginFlow is null;

    partial void OnActionLabelChanged(string? value) => OnPropertyChanged(nameof(HasAction));

    partial void OnActionCommandChanged(IRelayCommand? value) => OnPropertyChanged(nameof(HasAction));

    // --- Login flow (AC-713) ----------------------------------------------------------------------------------
    // Set once the row's "Login" action started an `ILoginFlow`; the row then renders this instead of the button.

    [ObservableProperty]
    private LoginFlowRowViewModel? _loginFlow;

    public bool HasLoginFlow => LoginFlow is not null;

    // Disposes the outgoing flow — its `ILoginFlow` owns a real `claude`/`codex` subprocess, which must not be
    // orphaned just because the row moved on to a different attempt.
    partial void OnLoginFlowChanging(LoginFlowRowViewModel? oldValue, LoginFlowRowViewModel? newValue)
    {
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            _ = oldValue.DisposeAsync().AsTask();
        }
    }

    partial void OnLoginFlowChanged(LoginFlowRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasAction));
        OnPropertyChanged(nameof(HasLoginFlow));
    }

    partial void OnQuestionPromptsChanged(IReadOnlyList<AskUserQuestionViewModel>? value)
    {
        foreach (var prompt in value ?? [])
        {
            prompt.AnswerChanged = () => OnPropertyChanged(nameof(CanSubmitAnswers));
        }

        OnPropertyChanged(nameof(HasQuestionPrompts));
        OnPropertyChanged(nameof(QuestionBranch));
        OnPropertyChanged(nameof(ToolBranch));
        OnPropertyChanged(nameof(IsPlainNonMarkdown));
        _RaiseReadingLevelPresentation();
    }

    // AC-146 nests sub-agent events under their Task/Agent row by ParentToolUseId and keeps them collapsed by default.

    private ObservableCollection<TranscriptEntryViewModel>? _subAgentRows;

    // Events belonging to the sub-agent this tool-use row spawned, in arrival order.
    public ObservableCollection<TranscriptEntryViewModel> SubAgentRows
    {
        get
        {
            if (_subAgentRows is not null)
            {
                return _subAgentRows;
            }

            _subAgentRows = [];
            _subAgentRows.CollectionChanged += _OnSubAgentRowsChanged;
            OnPropertyChanged(nameof(SubAgentRowsForDisplay));
            return _subAgentRows;
        }
    }

    public IEnumerable<TranscriptEntryViewModel> SubAgentRowsForDisplay => _subAgentRows ?? (IEnumerable<TranscriptEntryViewModel>)Array.Empty<TranscriptEntryViewModel>();

    // True once at least one sub-agent event has arrived — the anchor row shows its expand toggle only then.
    public bool HasSubAgentRows => _subAgentRows is { Count: > 0 };

    // Collapsed by default; the operator expands to see the sub-agent's own activity.
    [ObservableProperty]
    private bool _isSubAgentExpanded;

    // The expand toggle's label, e.g. "3 sub-agent events".
    public string SubAgentSummaryText
    {
        get
        {
            var count = _subAgentRows?.Count ?? 0;
            return $"{count} sub-agent event{(count == 1 ? "" : "s")}";
        }
    }

    // Chevron for the sub-agent toggle, matching the expanded/collapsed state.
    public MaterialIconKind SubAgentToggleIconKind => IsSubAgentExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    private void _OnSubAgentRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // AC-990: a nested row never joins the transcript, so the anchor hands its own session down — that is
        // what its consent buttons bind to.
        if (e.NewItems is not null)
        {
            foreach (TranscriptEntryViewModel nested in e.NewItems)
            {
                nested.Session = Session;

                // AC-1090: a nested row never joins the transcript, so nothing else watches it — without this its
                // own later changes (a result arriving) would never reach whoever is recording the anchor.
                nested.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SubAgentRowsForDisplay));
            }
        }

        OnPropertyChanged(nameof(HasSubAgentRows));
        OnPropertyChanged(nameof(SubAgentSummaryText));
    }

    [RelayCommand]
    private void ToggleSubAgentExpanded() => IsSubAgentExpanded = !IsSubAgentExpanded;

    partial void OnIsSubAgentExpandedChanged(bool value) => OnPropertyChanged(nameof(SubAgentToggleIconKind));

    // Tool name for a tool-use row; used to build the always-allow rule label.
    public string? ToolName { get; init; }

    // The proposed tool input as raw JSON; needed to build an exact-scope always-allow rule.
    public string? InputJson { get; init; }

    // AC-990: the session this row belongs to, stamped on when it joins the transcript. The row view reads it
    // from here instead of walking up to its host, which cannot answer while a row is being realised.
    public SessionViewModel? Session { get; set; }

    // When this row was created — its arrival time, shown as a small timestamp when the operator enables it (T7).
    public DateTimeOffset Timestamp { get; }

    // The `Timestamp` as a short wall-clock label (e.g. "14:07") for the transcript row.
    public string TimestampText => Timestamp.ToString("HH:mm");

    public TranscriptEntryViewModel(TranscriptEntryKind kind, string text)
        : this(kind, text, DateTimeOffset.Now)
    {
    }

    // Test seam: fix the arrival timestamp so the "HH:mm" label is deterministic.
    internal TranscriptEntryViewModel(TranscriptEntryKind kind, string text, DateTimeOffset timestamp)
    {
        Kind = kind;
        _text = text;
        Timestamp = timestamp;
    }

    public void AppendText(string delta)
    {
        Text += delta;
    }

    // Couples a tool result to this tool-use row (L14), matched on tool_use_id in the session view model.
    public void SetResult(string content, bool isError)
    {
        IsResultError = isError;
        BackgroundTaskId = _BackgroundTaskId(content);
        ResultText = content;
        OnPropertyChanged(nameof(IsBackgroundTool));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // Keeps the computed toggle icon/label/header in sync — they are computed, not observable, on their own.
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleIconKind));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToolHeader));
        // AC-489: the consent fold rides this same toggle, so opening it has to reveal the command it names.
        OnPropertyChanged(nameof(ShowConsentCommand));
        OnPropertyChanged(nameof(ConsentCommandToggleLabel));
    }

    partial void OnResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultDisplayText));
        OnPropertyChanged(nameof(ResultIsCodeLike));
        OnPropertyChanged(nameof(BackgroundStatusText));
    }

    partial void OnIsResultErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundStatusText));
        OnPropertyChanged(nameof(ToolResultPrefix));
    }

    partial void OnIsBackgroundTaskLiveChanged(bool value) => OnPropertyChanged(nameof(BackgroundStatusText));

    partial void OnBackgroundNotificationStatusChanged(BackgroundTaskStatus? value) => OnPropertyChanged(nameof(BackgroundStatusText));

    // AC-138 changes presentation, not history: Developer shows all, Focus groups tools, Simple omits tool noise.
    // The session view model alone forms neighbouring-row groups and pushes this level to rows.

    // The reading level this row renders at (AC-138); set by the owning `SessionViewModel`.
    [ObservableProperty]
    private ReadingLevel _readingLevel = ReadingLevel.Developer;

    // True when this row anchors a folded run of auto tool calls (Focus) — it carries the "N steps run" line and the expand toggle.
    [ObservableProperty]
    private bool _isGroupAnchor;

    // True when this row is part of a folded run of auto tool calls (Focus): the anchor or one of its members.
    [ObservableProperty]
    private bool _isInGroup;

    // How many auto tool calls the run this row anchors contains — shown as "N steps run".
    [ObservableProperty]
    private int _groupCount;

    // Whether this row's fold group is expanded; the view model flips it on every member of the run together.
    [ObservableProperty]
    private bool _isGroupExpanded;

    // Set by the view model on an anchor: flips the whole run's `IsGroupExpanded` when the "N steps run" line is clicked.
    public Action? GroupToggleRequested { get; set; }

    // A tool call that asked for approval — pending, or already allowed/denied. These stay visible at every reading level (AC-138).
    public bool RequiredApproval => IsToolUse && (IsPendingPermission || !string.IsNullOrEmpty(PermissionDecision));

    // A tool call that ran without asking (never prompted for permission) — the "noise" Focus folds and Simple hides.
    public bool IsAutoTool => IsToolUse && !RequiredApproval;

    // Whether the whole row shows at the current level. Text/user/assistant rows always show; an auto tool call
    // is folded (Focus, when a non-anchor member of a collapsed run) or hidden (Simple), while a consent tool call
    // stays visible everywhere. A standalone tool result is treated as auto noise and hidden only in Simple.
    public bool IsRowVisible => Kind switch
    {
        // Reasoning/thinking (AC-213): the developer surface only. Focus and Simple stay calm (AC-138) —
        // the row is still added to the transcript at every level, but it renders hidden below Developer.
        TranscriptEntryKind.Thinking => ReadingLevel == ReadingLevel.Developer,
        TranscriptEntryKind.ToolResult => ReadingLevel != ReadingLevel.Simple,
        TranscriptEntryKind.ToolUse => ReadingLevel switch
        {
            ReadingLevel.Simple => RequiredApproval,
            ReadingLevel.Focus => !(IsInGroup && !IsGroupAnchor) || IsGroupExpanded,
            _ => true,
        },
        _ => true,
    };

    // Whether the normal tool chip + expandable body shows: Developer always, Focus only when a grouped row is expanded, never in Simple (which speaks consent as a plain line and hides auto tools). A question card replaces the chip entirely (AC-715) — the chip's raw JSON is the same questions, spelled worse.
    public bool ShowToolBlock => IsToolUse && !HasQuestionPrompts && ReadingLevel switch
    {
        ReadingLevel.Simple => false,
        ReadingLevel.Focus => !IsInGroup || IsGroupExpanded,
        _ => true,
    };

    // Whether this row shows the "N steps run" fold line — only the anchor of a run, at the Focus level.
    public bool ShowGroupSummary => ReadingLevel == ReadingLevel.Focus && IsInGroup && IsGroupAnchor;

    // The fold line's label, e.g. "3 steps run".
    public string GroupSummaryText => $"{GroupCount} steps run";

    // Chevron for the fold line, matching the expanded/collapsed state.
    public MaterialIconKind GroupToggleIconKind => IsGroupExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    // Whether this row shows the plain-language consent line instead of the tool chip — a consent tool call, at the Simple level. A question card carries its own words at every level, so it needs no stand-in. AC-489: a call the plain-language card below could restate speaks for itself, so the coarse line stands down for it and stays as the fallback for everything else.
    public bool ShowHumanToolLine => _ShowsConsentAtSimple && !ShowPlainConsentCard;

    // --- Plain-language consent (AC-489) ---------------------------------------------------------------------

    // The call restated from itself, computed once: `ToolName` and `InputJson` are both set at construction, so
    // a row's sentence cannot change under it. Null for a call that cannot be read plainly.
    private PlainToolRequest? PlainRequest => _plainRequestResolved
        ? _plainRequest
        : _Resolve();

    private PlainToolRequest? _plainRequest;
    private bool _plainRequestResolved;

    private PlainToolRequest? _Resolve()
    {
        _plainRequest = PlainToolRequest.Describe(ToolName, InputJson);
        _plainRequestResolved = true;
        return _plainRequest;
    }

    // A consent tool call at the Simple level: where a non-developer meets this decision at all.
    private bool _ShowsConsentAtSimple => ReadingLevel == ReadingLevel.Simple && RequiredApproval && !HasQuestionPrompts;

    // Every reading level, not only Simple: one approval screen for both audiences. Pending only — once answered
    // the row goes back to the line saying which way it went, which a sentence on its own would drop.
    public bool ShowPlainConsentCard => IsPendingToolPermission && PlainRequest is not null;

    public string PlainConsentSentence => PlainRequest?.Sentence ?? string.Empty;

    public IReadOnlyList<string> PlainConsentPaths => PlainRequest?.Paths ?? [];

    public bool HasPlainConsentPaths => PlainConsentPaths.Count > 0;

    // One block rather than an items panel: these are lines to read and copy, not rows to interact with.
    public string PlainConsentPathsText => string.Join(Environment.NewLine, PlainConsentPaths);

    // The raw call, folded but never hidden — derived sentence or not, answered or not. Simple only: the tool
    // chip is already that fold at the other two levels.
    public bool ShowConsentCommandFold => _ShowsConsentAtSimple;

    public bool ShowConsentCommand => _ShowsConsentAtSimple && IsExpanded;

    public string ConsentCommandToggleLabel => IsExpanded ? "Hide the command" : "Show the command";

    // The consent decision in plain words for the Simple level (AC-138): what the tool did, and that the operator
    // approved, declined, or is being asked — e.g. "✓ Changed a file — you approved this". Jargon tool names map
    // to human actions; an unmapped tool falls back to its own name rather than inventing one.
    public string HumanToolText
    {
        get
        {
            var action = _HumanToolAction(ToolName);
            if (IsPendingPermission)
            {
                return $"{action} — waiting for your approval";
            }

            return (PermissionDecision ?? string.Empty).StartsWith("Den", StringComparison.OrdinalIgnoreCase)
                ? $"✕ {action} — you declined this"
                : $"✓ {action} — you approved this";
        }
    }

    [RelayCommand]
    private void ToggleGroup() => GroupToggleRequested?.Invoke();

    partial void OnReadingLevelChanged(ReadingLevel value) => _RaiseReadingLevelPresentation();

    partial void OnIsInGroupChanged(bool value) => _RaiseReadingLevelPresentation();

    partial void OnIsGroupAnchorChanged(bool value) => _RaiseReadingLevelPresentation();

    partial void OnGroupCountChanged(int value) => OnPropertyChanged(nameof(GroupSummaryText));

    partial void OnIsGroupExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(GroupToggleIconKind));
        _RaiseReadingLevelPresentation();
    }

    partial void OnIsPendingPermissionChanged(bool value) => _RaiseReadingLevelPresentation();

    partial void OnPermissionDecisionChanged(string? value) => _RaiseReadingLevelPresentation();

    // One place to re-announce every computed flag the reading level (or a permission/grouping change) affects, so a
    // level switch or a just-resolved consent updates the row's visibility and its plain-language line in one go.
    private void _RaiseReadingLevelPresentation()
    {
        OnPropertyChanged(nameof(RequiredApproval));
        OnPropertyChanged(nameof(IsAutoTool));
        OnPropertyChanged(nameof(IsRowVisible));
        OnPropertyChanged(nameof(ShowToolBlock));
        OnPropertyChanged(nameof(ToolBlockBranch));
        OnPropertyChanged(nameof(ShowGroupSummary));
        OnPropertyChanged(nameof(ShowHumanToolLine));
        OnPropertyChanged(nameof(HumanToolText));
        OnPropertyChanged(nameof(ShowPlainConsentCard));
        OnPropertyChanged(nameof(ShowConsentCommandFold));
        OnPropertyChanged(nameof(ShowConsentCommand));
        OnPropertyChanged(nameof(IsPendingToolPermission));
        OnPropertyChanged(nameof(PermissionBranch));
        OnPropertyChanged(nameof(IsAwaitingAnswer));
        OnPropertyChanged(nameof(CanSubmitAnswers));
    }

    // Maps a tool name to a plain-language action for the Simple consent line; an unmapped tool keeps its own name.
    private static string _HumanToolAction(string? toolName) => toolName switch
    {
        "Bash" => "Ran a command",
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "Changed a file",
        "Read" or "Glob" or "Grep" or "LS" => "Looked something up",
        "WebFetch" or "WebSearch" => "Looked something up online",
        null or "" => "Did something",
        _ => toolName,
    };

    // The input keys probed for the collapsed-header hint, in priority order. Static so a call does not
    // reallocate the same eight-string array on every collapsed tool-row render.
    private static readonly string[] ToolSummaryKeys =
        ["command", "file_path", "path", "pattern", "url", "query", "description", "prompt"];

    // The first meaningful input value (command/file/pattern/…), truncated — the collapsed-header hint.
    private static string _ToolSummary(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var key in ToolSummaryKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? string.Empty;
                    return text.Length > 80 ? text[..80] + "…" : text;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (or malformed): no hint, the full input is still shown once expanded.
        }

        return string.Empty;
    }

    // Whether the call asked to be run outside the turn (AC-1056) — `Bash`'s own `run_in_background` flag,
    // read off the same input JSON the header hint above comes from.
    private static bool _RequestsBackground(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("run_in_background", out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The two sentences a hand-off to the background is announced with, in the tool result itself: "Command
    // running in background with ID: <id>" (Bash) and "moved to the background as task <id>" (an MCP tool that
    // overran, AC-1053). Measured against the real CLI rather than taken from documentation.
    private static readonly Regex BackgroundTaskIdPattern = new(
        @"background\s+(?:with\s+ID:\s*|as\s+task\s+)([A-Za-z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The provider's background-task id from a result that announces one, else null.
    private static string? _BackgroundTaskId(string? resultText)
    {
        if (string.IsNullOrEmpty(resultText))
        {
            return null;
        }

        var match = BackgroundTaskIdPattern.Match(resultText);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Pretty-prints a JSON result for readability; leaves anything else untouched.
    private static string _FormatResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        var trimmed = result.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(result);
            return JsonSerializer.Serialize(doc.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return result;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}
