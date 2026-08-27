namespace Cockpit.App.ViewModels;

// Built from the built-in app actions and the plugin-contributed shortcuts, so plugins populate the palette simply by
// registering shortcuts (a shortcut with no gesture is a palette-only command).
public sealed record PaletteCommand(string Title, string GestureDisplay, Action Invoke);
