using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Kind of a single line rendered in the Claude session transcript view.
/// </summary>
public enum TranscriptEntryKind
{
    AssistantText,
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

    /// <summary>Plain rows rendered as a single wrapped text block (assistant/user text — not thinking, a tool-use, or a standalone tool result).</summary>
    public bool IsPlainText => !IsThinking && !IsToolResult && !IsToolUse;

    /// <summary>
    /// Presentational only: the echoed user message is an <see cref="TranscriptEntryKind.AssistantText"/>
    /// row prefixed with "&gt; " (see <c>ClaudeSessionViewModel.SendAsync</c>) — styled muted so it reads
    /// as the user's own line rather than assistant output.
    /// </summary>
    public bool IsUserRow => Kind == TranscriptEntryKind.AssistantText && Text.StartsWith("> ", StringComparison.Ordinal);

    /// <summary>Chevron glyph for a standalone (orphan) tool-result row; presentational only.</summary>
    public string ToggleGlyph => IsExpanded ? "▾ Tool result" : "▸ Tool result (click to show)";

    /// <summary>Chevron for the result coupled to a tool-use row (L14) — reads "error" when the result failed.</summary>
    public string ResultToggleGlyph => (IsExpanded ? "▾ " : "▸ ") + (IsResultError ? "error" : "result");

    /// <summary>The tool result coupled to this tool-use row by tool_use_id (L14), or null until it arrives.</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>True when the coupled tool result reported an error.</summary>
    [ObservableProperty]
    private bool _isResultError;

    /// <summary>True once a result has been coupled to this tool-use row, driving its expandable result section.</summary>
    public bool HasResult => ResultText is not null;

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

    public TranscriptEntryViewModel(TranscriptEntryKind kind, string text)
    {
        Kind = kind;
        _text = text;
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

    /// <summary>Keeps the computed chevron glyphs in sync — they are computed, not observable, on their own.</summary>
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ResultToggleGlyph));
    }

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    partial void OnIsResultErrorChanged(bool value) => OnPropertyChanged(nameof(ResultToggleGlyph));
}
