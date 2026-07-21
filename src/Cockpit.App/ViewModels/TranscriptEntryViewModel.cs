using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;
using Material.Icons;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Kind of a single line rendered in the Claude session transcript view.
/// </summary>
public enum TranscriptEntryKind
{
    AssistantText,
    UserText,
    ToolUse,
    ToolResult,
    Question,
    TurnCompleted,
    Error,
}

/// <summary>
/// A single row in the transcript view. Assistant text entries are mutated in place
/// (<see cref="AppendText"/>) so streaming deltas render as growing text rather than
/// as new rows.
/// </summary>
public partial class TranscriptEntryViewModel : ViewModelBase
{
    public TranscriptEntryKind Kind { get; }

    public bool IsToolResult => Kind == TranscriptEntryKind.ToolResult;

    public bool IsToolUse => Kind == TranscriptEntryKind.ToolUse;

    /// <summary>Rows not rendered as a tool-use or a standalone tool result — assistant/user text, questions, errors.</summary>
    public bool IsPlainText => !IsToolResult && !IsToolUse;

    /// <summary>Assistant prose renders as markdown (T9).</summary>
    public bool IsAssistantMarkdown => Kind == TranscriptEntryKind.AssistantText;

    /// <summary>The user's own message, rendered as a right-aligned bubble (T2) — plain text, not markdown.</summary>
    public bool IsUserRow => Kind == TranscriptEntryKind.UserText;

    /// <summary>Plain rows that are neither the user bubble nor markdown: questions, errors, turn results.</summary>
    public bool IsPlainNonMarkdown => IsPlainText && !IsAssistantMarkdown && !IsUserRow;

    /// <summary>
    /// Rows whose arrival timestamp renders at the top of the row (assistant prose, questions/errors/turn
    /// results). User and tool-use rows carry their timestamp inline in their own header line instead
    /// (AC-144), so the generic top-row timestamp is suppressed for them to avoid a doubled label.
    /// </summary>
    public bool IsTopTimestampRow => !IsUserRow && !IsToolUse;

    /// <summary>Chevron icon for a row's expand/collapse toggle, shared by the tool-use header and the standalone tool-result row.</summary>
    public MaterialIconKind ToggleIconKind => IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    /// <summary>Label for a standalone (orphan) tool-result row's toggle; the chevron itself renders separately as <see cref="ToggleIconKind"/>.</summary>
    public string ToggleLabel => IsExpanded ? "Tool result" : "Tool result (click to show)";

    /// <summary>
    /// Compact one-line label for a collapsed tool-use row (T5): tool name + a short hint pulled from the
    /// input (command/file/pattern/…), so a call reads as "Bash · dotnet build" instead of the full input
    /// JSON. The full input shows once expanded; the row's own chevron renders separately as <see cref="ToggleIconKind"/>.
    /// </summary>
    public string ToolHeader
    {
        get
        {
            var name = string.IsNullOrEmpty(ToolName) ? "Tool" : ToolName;
            var summary = _ToolSummary(InputJson);
            return summary.Length == 0 ? name : $"{name}  ·  {summary}";
        }
    }

    /// <summary>The tool result coupled to this tool-use row by tool_use_id (L14), or null until it arrives.</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>True when the coupled tool result reported an error.</summary>
    [ObservableProperty]
    private bool _isResultError;

    /// <summary>True once a result has been coupled to this tool-use row, driving its expandable result section.</summary>
    public bool HasResult => ResultText is not null;

    /// <summary>
    /// The result as it should be shown (T6): JSON is pretty-printed for readability, everything else
    /// is passed through unchanged. Kept separate from the raw <see cref="ResultText"/> so the copy
    /// button hands the operator the same formatted text they see.
    /// </summary>
    public string ResultDisplayText => _FormatResult(ResultText);

    /// <summary>
    /// True when the result reads as structured/code (JSON, multi-line, or long) and so should render
    /// in a monospace code box with a copy button rather than as a wrapped paragraph (T6).
    /// </summary>
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

    /// <summary>Collapsed by default for tool-use and standalone tool-result rows, so their input/output stays folded until the operator expands the chip.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Non-null only for <see cref="TranscriptEntryKind.ToolUse"/> rows awaiting an allow/deny decision.</summary>
    [ObservableProperty]
    private bool _isPendingPermission;

    [ObservableProperty]
    private string? _permissionDecision;

    public string? ToolUseId { get; init; }

    /// <summary>Tool name for a tool-use row; used to build the always-allow rule label.</summary>
    public string? ToolName { get; init; }

    /// <summary>The proposed tool input as raw JSON; needed to build an exact-scope always-allow rule.</summary>
    public string? InputJson { get; init; }

    /// <summary>When this row was created — its arrival time, shown as a small timestamp when the operator enables it (T7).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The <see cref="Timestamp"/> as a short wall-clock label (e.g. "14:07") for the transcript row.</summary>
    public string TimestampText => Timestamp.ToString("HH:mm");

    public TranscriptEntryViewModel(TranscriptEntryKind kind, string text)
        : this(kind, text, DateTimeOffset.Now)
    {
    }

    /// <summary>Test seam: fix the arrival timestamp so the "HH:mm" label is deterministic.</summary>
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

    /// <summary>Couples a tool result to this tool-use row (L14), matched on tool_use_id in the session view model.</summary>
    public void SetResult(string content, bool isError)
    {
        IsResultError = isError;
        ResultText = content;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Keeps the computed toggle icon/label/header in sync — they are computed, not observable, on their own.</summary>
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

    /// <summary>The reading level this row renders at (AC-138); set by the owning <see cref="SessionViewModel"/>.</summary>
    [ObservableProperty]
    private ReadingLevel _readingLevel = ReadingLevel.Developer;

    /// <summary>True when this row anchors a folded run of auto tool calls (Focus) — it carries the "N steps run" line and the expand toggle.</summary>
    [ObservableProperty]
    private bool _isGroupAnchor;

    /// <summary>True when this row is part of a folded run of auto tool calls (Focus): the anchor or one of its members.</summary>
    [ObservableProperty]
    private bool _isInGroup;

    /// <summary>How many auto tool calls the run this row anchors contains — shown as "N steps run".</summary>
    [ObservableProperty]
    private int _groupCount;

    /// <summary>Whether this row's fold group is expanded; the view model flips it on every member of the run together.</summary>
    [ObservableProperty]
    private bool _isGroupExpanded;

    /// <summary>Set by the view model on an anchor: flips the whole run's <see cref="IsGroupExpanded"/> when the "N steps run" line is clicked.</summary>
    public Action? GroupToggleRequested { get; set; }

    /// <summary>A tool call that asked for approval — pending, or already allowed/denied. These stay visible at every reading level (AC-138).</summary>
    public bool RequiredApproval => IsToolUse && (IsPendingPermission || !string.IsNullOrEmpty(PermissionDecision));

    /// <summary>A tool call that ran without asking (never prompted for permission) — the "noise" Focus folds and Simple hides.</summary>
    public bool IsAutoTool => IsToolUse && !RequiredApproval;

    /// <summary>
    /// Whether the whole row shows at the current level. Text/user/assistant rows always show; an auto tool call
    /// is folded (Focus, when a non-anchor member of a collapsed run) or hidden (Simple), while a consent tool call
    /// stays visible everywhere. A standalone tool result is treated as auto noise and hidden only in Simple.
    /// </summary>
    public bool IsRowVisible => Kind switch
    {
        TranscriptEntryKind.ToolResult => ReadingLevel != ReadingLevel.Simple,
        TranscriptEntryKind.ToolUse => ReadingLevel switch
        {
            ReadingLevel.Simple => RequiredApproval,
            ReadingLevel.Focus => !(IsInGroup && !IsGroupAnchor) || IsGroupExpanded,
            _ => true,
        },
        _ => true,
    };

    /// <summary>Whether the normal tool chip + expandable body shows: Developer always, Focus only when a grouped row is expanded, never in Simple (which speaks consent as a plain line and hides auto tools).</summary>
    public bool ShowToolBlock => IsToolUse && ReadingLevel switch
    {
        ReadingLevel.Simple => false,
        ReadingLevel.Focus => !IsInGroup || IsGroupExpanded,
        _ => true,
    };

    /// <summary>Whether this row shows the "N steps run" fold line — only the anchor of a run, at the Focus level.</summary>
    public bool ShowGroupSummary => ReadingLevel == ReadingLevel.Focus && IsInGroup && IsGroupAnchor;

    /// <summary>The fold line's label, e.g. "3 steps run".</summary>
    public string GroupSummaryText => $"{GroupCount} steps run";

    /// <summary>Chevron for the fold line, matching the expanded/collapsed state.</summary>
    public MaterialIconKind GroupToggleIconKind => IsGroupExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    /// <summary>Whether this row shows the plain-language consent line instead of the tool chip — a consent tool call, at the Simple level.</summary>
    public bool ShowHumanToolLine => ReadingLevel == ReadingLevel.Simple && RequiredApproval;

    /// <summary>
    /// The consent decision in plain words for the Simple level (AC-138): what the tool did, and that the operator
    /// approved, declined, or is being asked — e.g. "✓ Changed a file — you approved this". Jargon tool names map
    /// to human actions; an unmapped tool falls back to its own name rather than inventing one.
    /// </summary>
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
    }

    /// <summary>Maps a tool name to a plain-language action for the Simple consent line; an unmapped tool keeps its own name.</summary>
    private static string _HumanToolAction(string? toolName) => toolName switch
    {
        "Bash" => "Ran a command",
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "Changed a file",
        "Read" or "Glob" or "Grep" or "LS" => "Looked something up",
        "WebFetch" or "WebSearch" => "Looked something up online",
        null or "" => "Did something",
        _ => toolName,
    };

    /// <summary>The first meaningful input value (command/file/pattern/…), truncated — the collapsed-header hint.</summary>
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

    /// <summary>Pretty-prints a JSON result for readability; leaves anything else untouched.</summary>
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
