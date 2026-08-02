namespace Cockpit.App.ViewModels;

// One entry in the command palette (#: command palette): a `Title`, its keyboard shortcut for
// display (`GestureDisplay`, blank when unbound), and the `Invoke` to run when it is
// chosen. Built from the built-in app actions and the plugin-contributed shortcuts, so plugins populate the
// palette simply by registering shortcuts (a shortcut with no gesture is a palette-only command).
public sealed record PaletteCommand(string Title, string GestureDisplay, Action Invoke);
