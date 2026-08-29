namespace Cockpit.App.ViewModels;

// Options default-shell choice (AC-25): record equality lets the ComboBox reselect the saved shell id after reload.
public sealed record TerminalShellChoice(string Label, string Value);
