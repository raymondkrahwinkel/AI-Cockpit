using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Zyra.Voice.App.ViewModels;

/// <summary>
/// Kind of a single line rendered in the Claude session transcript view.
/// </summary>
public enum TranscriptEntryKind
{
    AssistantText,
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

    [ObservableProperty]
    private string _text;

    /// <summary>Non-null only for <see cref="TranscriptEntryKind.ToolUse"/> rows awaiting an allow/deny decision.</summary>
    [ObservableProperty]
    private bool _isPendingPermission;

    [ObservableProperty]
    private string? _permissionDecision;

    public string? ToolUseId { get; init; }

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
    private void Allow() => PermissionDecision = "Allowed";

    [RelayCommand]
    private void Deny() => PermissionDecision = "Denied";
}
