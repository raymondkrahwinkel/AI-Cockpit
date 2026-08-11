using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
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

    // A rule across the transcript marking a break in the conversation the transcript itself keeps recording —
    // today only "context cleared" (AC-564). The transcript stays whole because it is the pane's audit surface;
    // this row is what says where the agent's memory stops. Visible at every reading level: it explains
    // everything below it.
    Divider,
}

// A single row in the transcript view. Assistant text entries are mutated in place
// (`AppendText`) so streaming deltas render as growing text rather than
// as new rows.
public partial class TranscriptEntryViewModel : ViewModelBase
{
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

    // Plain rows that are neither the user bubble nor markdown: questions and turn results. An error row
    // (AC-720) is plain text too, but gets its own severity-coloured card instead of this branch.
    public bool IsPlainNonMarkdown => IsPlainText && !IsAssistantMarkdown && !IsUserRow && !IsErrorRow;

    // Rows whose arrival timestamp renders at the top of the row (assistant prose, questions/errors/turn
    // results). User and tool-use rows carry their timestamp inline in their own header line instead
    // (AC-144), so the generic top-row timestamp is suppressed for them to avoid a doubled label.
    public bool IsTopTimestampRow => !IsUserRow && !IsToolUse && !IsThinking;

    // Chevron icon for a row's expand/collapse toggle, shared by the tool-use header and the standalone tool-result row.
    public MaterialIconKind ToggleIconKind => IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    // Label for a standalone (orphan) tool-result row's toggle; the chevron itself renders separately as `ToggleIconKind`.
    public string ToggleLabel => IsExpanded ? "Tool result" : "Tool result (click to show)";

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

    // --- Clarifying questions (AC-715) ------------------------------------------------------------------------
    // An AskUserQuestion rides the permission callback like a tool approval, but Allow/Deny is not an answer to it:
    // a row carrying parsed questions renders them as its own card, and keeps them there once answered.

    [ObservableProperty]
    private IReadOnlyList<AskUserQuestionViewModel>? _questionPrompts;

    public bool HasQuestionPrompts => QuestionPrompts is { Count: > 0 };

    // The generic allow/deny row: every pending consent except a question, which has its own Send.
    public bool IsPendingToolPermission => IsPendingPermission && !HasQuestionPrompts;

    // Send only lights up once every question on the card has an answer — a half-filled card would send the agent
    // an answers object missing the keys it asked about.
    public bool CanSubmitAnswers =>
        IsPendingPermission && QuestionPrompts is { Count: > 0 } prompts && prompts.All(prompt => prompt.HasAnswer);

    // --- Error severity (AC-720) ------------------------------------------------------------------------------
    // The driver's own Kind, or the host's text-heuristic guess when it hasn't set one — presentation only,
    // never behaviour (SessionViewModel resolves which of the two this is before setting it).

    [ObservableProperty]
    private SessionErrorKind _errorKind = SessionErrorKind.Unknown;

    [ObservableProperty]
    private DateTimeOffset? _retryAfter;

    // Auth/config problems block the session until the operator acts — the row that gets the "Log in…" action.
    public bool IsBlockingError => IsErrorRow && ErrorKind == SessionErrorKind.AuthRequired;

    // Rate limits and outages resolve on their own; the operator's own next attempt is the only "action".
    public bool IsTemporaryError => IsErrorRow && ErrorKind is SessionErrorKind.RateLimited or SessionErrorKind.ServiceUnavailable;

    // Everything else — a parse failure, an empty reply, an unclassified driver — and always the safe
    // default: never guessed red or amber (AC-720 acceptance criterion).
    public bool IsInformationalError => IsErrorRow && !IsBlockingError && !IsTemporaryError;

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
        _RaiseReadingLevelPresentation();
    }

    // --- Sub-agent nesting (AC-146) ---------------------------------------------------------------------------
    // A Task/Agent tool call's own row anchors whatever activity the sub-agent it spawned produced — its own
    // tool calls, text and thinking, matched to this row by SessionEvent.ParentToolUseId == this row's own
    // ToolUseId. Nested here rather than flattened into the top-level Transcript, and collapsed by default
    // (Raymond, 2026-07-29): an operator sees that a sub-agent ran, and expands to see what it did.

    // Events belonging to the sub-agent this tool-use row spawned, in arrival order.
    public ObservableCollection<TranscriptEntryViewModel> SubAgentRows { get; } = [];

    // True once at least one sub-agent event has arrived — the anchor row shows its expand toggle only then.
    public bool HasSubAgentRows => SubAgentRows.Count > 0;

    // Collapsed by default; the operator expands to see the sub-agent's own activity.
    [ObservableProperty]
    private bool _isSubAgentExpanded;

    // The expand toggle's label, e.g. "3 sub-agent events".
    public string SubAgentSummaryText => $"{SubAgentRows.Count} sub-agent event{(SubAgentRows.Count == 1 ? "" : "s")}";

    // Chevron for the sub-agent toggle, matching the expanded/collapsed state.
    public MaterialIconKind SubAgentToggleIconKind => IsSubAgentExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    private void _OnSubAgentRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
        SubAgentRows.CollectionChanged += _OnSubAgentRowsChanged;
    }

    public void AppendText(string delta)
    {
        Text += delta;
    }

    // Couples a tool result to this tool-use row (L14), matched on tool_use_id in the session view model.
    public void SetResult(string content, bool isError)
    {
        IsResultError = isError;
        ResultText = content;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // Keeps the computed toggle icon/label/header in sync — they are computed, not observable, on their own.
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleIconKind));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToolHeader));
    }

    partial void OnResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultDisplayText));
        OnPropertyChanged(nameof(ResultIsCodeLike));
    }

    // --- Reading levels (AC-138) ------------------------------------------------------------------------------
    // The current reading level of the session this row belongs to, pushed onto every row by the session view model
    // when the level changes. It drives what the row shows without touching what the agent did: Developer shows
    // everything, Focus folds runs of auto tool calls and hides the "$" cost, Simple drops tool noise and speaks
    // consent decisions in plain words. The grouping fields below (anchor/count/expanded) are set by the view model,
    // which is the only thing that can see a row's neighbours to form a run.

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

    // Whether this row shows the plain-language consent line instead of the tool chip — a consent tool call, at the Simple level. A question card carries its own words at every level, so it needs no stand-in.
    public bool ShowHumanToolLine => ReadingLevel == ReadingLevel.Simple && RequiredApproval && !HasQuestionPrompts;

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
        OnPropertyChanged(nameof(ShowGroupSummary));
        OnPropertyChanged(nameof(ShowHumanToolLine));
        OnPropertyChanged(nameof(HumanToolText));
        OnPropertyChanged(nameof(IsPendingToolPermission));
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

            foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "query", "description", "prompt" })
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
