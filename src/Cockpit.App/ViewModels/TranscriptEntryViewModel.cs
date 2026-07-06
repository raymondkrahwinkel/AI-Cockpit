using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Kind of a single line rendered in the Claude session transcript view.
/// </summary>
public enum TranscriptEntryKind
{
    AssistantText,
    UserText,
    Thinking,
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

    public bool IsThinking => Kind == TranscriptEntryKind.Thinking;

    public bool IsToolResult => Kind == TranscriptEntryKind.ToolResult;

    public bool IsToolUse => Kind == TranscriptEntryKind.ToolUse;

    /// <summary>Rows not rendered as thinking, a tool-use, or a standalone tool result — assistant/user text, questions, errors.</summary>
    public bool IsPlainText => !IsThinking && !IsToolResult && !IsToolUse;

    /// <summary>Assistant prose renders as markdown (T9).</summary>
    public bool IsAssistantMarkdown => Kind == TranscriptEntryKind.AssistantText;

    /// <summary>The user's own message, rendered as a right-aligned bubble (T2) — plain text, not markdown.</summary>
    public bool IsUserRow => Kind == TranscriptEntryKind.UserText;

    /// <summary>Plain rows that are neither the user bubble nor markdown: questions, errors, turn results.</summary>
    public bool IsPlainNonMarkdown => IsPlainText && !IsAssistantMarkdown && !IsUserRow;

    /// <summary>Chevron glyph for a standalone (orphan) tool-result row; presentational only.</summary>
    public string ToggleGlyph => IsExpanded ? "▾ Tool result" : "▸ Tool result (click to show)";

    /// <summary>
    /// Compact one-line header for a collapsed tool-use row (T5): chevron + tool name + a short hint
    /// pulled from the input (command/file/pattern/…), so a call reads as "▸ Bash · dotnet build"
    /// instead of the full input JSON. The full input shows once expanded.
    /// </summary>
    public string ToolHeader
    {
        get
        {
            var glyph = IsExpanded ? "▾ " : "▸ ";
            var name = string.IsNullOrEmpty(ToolName) ? "Tool" : ToolName;
            var summary = _ToolSummary(InputJson);
            return summary.Length == 0 ? glyph + name : $"{glyph}{name}  ·  {summary}";
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

    /// <summary>Collapsed by default for thinking rows so they read as dimmed/collapsible, not as regular transcript text.</summary>
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

    /// <summary>Keeps the computed glyph/header in sync — they are computed, not observable, on their own.</summary>
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToolHeader));
    }

    partial void OnResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultDisplayText));
        OnPropertyChanged(nameof(ResultIsCodeLike));
    }

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
