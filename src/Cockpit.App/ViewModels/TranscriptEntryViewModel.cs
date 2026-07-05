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

    /// <summary>Plain rows rendered as a single wrapped text block (not thinking, not a collapsible tool result).</summary>
    public bool IsPlainText => !IsThinking && !IsToolResult;

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

    public TranscriptEntryViewModel(TranscriptEntryKind kind, string text)
    {
        Kind = kind;
        _text = text;
    }

    public void AppendText(string delta)
    {
        Text += delta;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Allow() => _SetDecision("Allowed");

    [RelayCommand]
    private void Deny() => _SetDecision("Denied");

    [RelayCommand]
    private void AllowAlwaysExact() => _SetDecision(ToolName is null ? "Always allowed (exact)" : $"Always allowed (exact: {ToolName})");

    [RelayCommand]
    private void AllowAlwaysWildcard() => _SetDecision(ToolName is null ? "Always allowed (wildcard)" : $"Always allowed ({ToolName}:*)");

    private void _SetDecision(string decision)
    {
        PermissionDecision = decision;
        IsPendingPermission = false;
    }
}
