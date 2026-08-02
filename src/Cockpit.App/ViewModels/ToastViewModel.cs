using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

// One shown toast (#61): the message/severity plus an optional action button. Values never change after
// construction — the only mutable behaviour is dismissal, raised via `Dismissed` so the owning
// `ToastHostViewModel` can remove it from its collection, whether triggered by the close
// button, the action button, or the host's auto-dismiss timer.
public sealed partial class ToastViewModel(string message, ToastSeverity severity, string? actionLabel, Action? onAction)
{
    private readonly Action? _onAction = onAction;

    public string Message { get; } = message;

    public ToastSeverity Severity { get; } = severity;

    public string? ActionLabel { get; } = actionLabel;

    // True only when both an action label and a callback were supplied — a label alone with no callback would be a dead button.
    public bool HasAction { get; } = !string.IsNullOrWhiteSpace(actionLabel) && onAction is not null;

    // Theme brush resource key for this severity (resolved by `StatusBrushConverter`), matching the session-status dot colours.
    public string BrushKey => Severity switch
    {
        ToastSeverity.Success => "CockpitStatusDoneBrush",
        ToastSeverity.Warning => "CockpitStatusWaitingBrush",
        ToastSeverity.Information => "CockpitStatusBusyBrush",
        ToastSeverity.Error => "CockpitStatusErrorBrush",
        _ => "CockpitTextFaintBrush",
    };

    // Small icon shown next to the message, mirroring the sidebar's status markers (e.g. the needs-attention warning).
    public MaterialIconKind Glyph => Severity switch
    {
        ToastSeverity.Success => MaterialIconKind.Check,
        ToastSeverity.Warning => MaterialIconKind.AlertOutline,
        ToastSeverity.Information => MaterialIconKind.InformationOutline,
        ToastSeverity.Error => MaterialIconKind.Close,
        _ => MaterialIconKind.Circle,
    };

    // Raised once, however dismissal happened (close button, action button, or auto-dismiss elapsing).
    public event EventHandler? Dismissed;

    // Bound to the toast's close (✕) button, and the target the host's auto-dismiss timer invokes.
    [RelayCommand]
    private void Close() => Dismissed?.Invoke(this, EventArgs.Empty);

    // Bound to the optional action button: runs the caller's callback, then dismisses like a normal close.
    [RelayCommand(CanExecute = nameof(HasAction))]
    private void InvokeAction()
    {
        _onAction?.Invoke();
        Close();
    }
}
