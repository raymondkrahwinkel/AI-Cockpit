using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Shortcuts;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in the Options → Shortcuts tab: an action's <see cref="Label"/> and its editable
/// <see cref="Gesture"/> string. App-action rows carry their <see cref="Action"/> and are editable; a
/// plugin-contributed shortcut row is shown read-only (<see cref="IsEditable"/> false) so the operator can see
/// what a plugin bound without the plugin's gesture being persisted here.
/// </summary>
public partial class ShortcutRowViewModel : ViewModelBase
{
    public string Label { get; }

    /// <summary>Non-null for a built-in app action; null for a plugin-contributed shortcut.</summary>
    public ShortcutAction? Action { get; }

    public bool IsEditable { get; }

    [ObservableProperty]
    private string _gesture;

    public ShortcutRowViewModel(string label, ShortcutAction action, string gesture)
    {
        Label = label;
        Action = action;
        _gesture = gesture;
        IsEditable = true;
    }

    /// <summary>Read-only row for a plugin-contributed shortcut.</summary>
    public ShortcutRowViewModel(string label, string gesture)
    {
        Label = label;
        Action = null;
        _gesture = gesture;
        IsEditable = false;
    }
}
