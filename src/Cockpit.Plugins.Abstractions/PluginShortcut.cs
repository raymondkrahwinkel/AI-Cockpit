namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A command a plugin contributes via <see cref="ICockpitHost.AddShortcut"/> — e.g. YouTrack binding
/// <c>Shift+Y</c> to open its dialog. The host registers <paramref name="DefaultGesture"/> and invokes
/// <paramref name="OnInvoke"/> (on the UI thread) when it is pressed, alongside the built-in app-action
/// shortcuts, and lists it in the command palette. Like the app shortcuts, a bound gesture only fires when the
/// operator is not typing into a text field or the terminal, so it never hijacks a keystroke. Leave
/// <paramref name="DefaultGesture"/> blank for a <em>palette-only command</em>: no keystroke is bound, but it
/// still appears in the command palette (and the operator can assign it a gesture in Options → Shortcuts).
/// </summary>
/// <param name="Id">Stable identifier for this command (e.g. "youtrack.open"), unique within the plugin.</param>
/// <param name="Title">Human-readable label shown in the command palette and the shortcuts list in Options.</param>
/// <param name="DefaultGesture">The gesture to bind, in Avalonia form (e.g. "Shift+Y", "Ctrl+Shift+K"). Blank = palette-only (no key bound).</param>
/// <param name="OnInvoke">Runs when the gesture is pressed or the command is chosen in the palette.</param>
public sealed record PluginShortcut(string Id, string Title, string DefaultGesture, Action OnInvoke);
