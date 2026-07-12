namespace Cockpit.App.ViewModels;

/// <summary>
/// One active keyboard shortcut the dispatcher can fire: the Avalonia-form <see cref="Gesture"/> string (e.g.
/// "Shift+N"), a <see cref="Label"/> for diagnostics, and the <see cref="Invoke"/> action to run. Built by
/// <see cref="CockpitViewModel"/> from the configured app-action gestures and the plugin-contributed shortcuts;
/// the view parses the gesture and matches it against key presses (keeping Avalonia key types out of the VM).
/// </summary>
public sealed record ShortcutBinding(string Gesture, string Label, Action Invoke);
