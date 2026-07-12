namespace Cockpit.App.ViewModels;

/// <summary>
/// One active keyboard shortcut the dispatcher can fire: the Avalonia-form <see cref="Gesture"/> string (e.g.
/// "Shift+N"), a <see cref="Label"/> for diagnostics, and the <see cref="Invoke"/> action to run. Built by
/// <see cref="CockpitViewModel"/> from the configured app-action gestures and the plugin-contributed shortcuts;
/// the view parses the gesture and matches it against key presses (keeping Avalonia key types out of the VM).
/// <see cref="AlwaysActive"/> lets a binding fire even while the operator is typing in a text field or the
/// terminal — used for the command palette, the one "reachable from anywhere" escape hatch.
/// <see cref="ActiveInTerminal"/> is the narrower version of that: the binding fires over the embedded
/// terminal but still stands down inside a text box — used by the session-switch shortcuts.
/// </summary>
public sealed record ShortcutBinding(
    string Gesture,
    string Label,
    Action Invoke,
    bool AlwaysActive = false,
    bool ActiveInTerminal = false);
