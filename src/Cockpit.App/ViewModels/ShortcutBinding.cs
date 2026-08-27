namespace Cockpit.App.ViewModels;

// Store gestures as strings to keep Avalonia key types out of the view model; the flags distinguish text-entry and
// terminal activation.
public sealed record ShortcutBinding(
    string Gesture,
    string Label,
    Action Invoke,
    bool AlwaysActive = false,
    bool ActiveInTerminal = false);
