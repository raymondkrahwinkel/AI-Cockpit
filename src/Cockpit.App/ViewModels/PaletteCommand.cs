namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the command palette (#: command palette): a <see cref="Title"/>, its keyboard shortcut for
/// display (<see cref="GestureDisplay"/>, blank when unbound), and the <see cref="Invoke"/> to run when it is
/// chosen. Built from the built-in app actions and the plugin-contributed shortcuts, so plugins populate the
/// palette simply by registering shortcuts (a shortcut with no gesture is a palette-only command).
/// </summary>
public sealed record PaletteCommand(string Title, string GestureDisplay, Action Invoke);
