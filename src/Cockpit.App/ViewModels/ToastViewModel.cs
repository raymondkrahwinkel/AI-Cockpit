using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

// Shown toast (#61): immutable message/severity/actions; Dismissed lets the host remove any dismissal route. AC-1549:
// any number of buttons, the first one primary, so a reminder can offer Done · Snooze 10 min · 1 hour.
public sealed partial class ToastViewModel
{
    public ToastViewModel(string message, ToastSeverity severity, string? actionLabel, Action? onAction)
        : this(message, severity, !string.IsNullOrWhiteSpace(actionLabel) && onAction is not null ? [new ToastAction(actionLabel, onAction)] : [])
    {
    }

    public ToastViewModel(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions)
    {
        Message = message;
        Severity = severity;
        Actions = [.. actions.Select((action, index) => new ToastActionViewModel(action.Label, index == 0, () =>
        {
            try
            {
                action.OnInvoke();
            }
            finally
            {
                Close();
            }
        }))];
        ActionLabel = actions.FirstOrDefault()?.Label;
        HasAction = Actions.Count > 0;
    }

    public IReadOnlyList<ToastActionViewModel> Actions { get; }

    public string Message { get; }

    // AC-1853: an optional bold line above the message; the message then reads as its secondary text.
    public string? Title { get; init; }

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    // The startup bundle's own parts: a quiet tail on the headline, one line per bundled item and a "+N more".
    public string? Note { get; init; }

    public IReadOnlyList<ToastLineViewModel> Lines { get; init; } = [];

    public bool HasLines => Lines.Count > 0;

    public string? MoreLabel { get; init; }

    public ToastSeverity Severity { get; }

    public string? ActionLabel { get; }

    // True only when both an action label and a callback were supplied — a label alone with no callback would be a dead button.
    public bool HasAction { get; }

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
    private void InvokeAction() => Actions[0].Invoke();
}

// One button on a toast; invoking it runs the action and closes the toast.
public sealed partial class ToastActionViewModel(string label, bool isPrimary, Action invoke)
{
    internal Func<bool> StillApplies { get; set; } = static () => true;

    private bool CanInvoke() => StillApplies();

    public string Label { get; } = label;

    public bool IsPrimary { get; } = isPrimary;

    [RelayCommand(CanExecute = nameof(CanInvoke))]
    public void Invoke()
    {
        if (StillApplies())
        {
            invoke();
        }
    }
}

// One item named in the startup bundle: its glyph, its source in bold, and its text.
public sealed record ToastLineViewModel(MaterialIconKind Glyph, string BrushKey, string Source, string Text);
