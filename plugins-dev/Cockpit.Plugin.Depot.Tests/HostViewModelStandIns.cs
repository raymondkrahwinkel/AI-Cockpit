namespace Cockpit.App.ViewModels;

// AC-1593 gave the shared Theme.axaml a style typed on the host's SelectableChoice (x:DataType). The app compiles
// that file inside Cockpit.App, where the type exists; HeadlessAvalonia parses it at runtime from a plugin test,
// which cannot reference Cockpit.App, so the runtime XAML compiler needs a type of that name and shape to bind.
// Nothing here renders a ComboBox.policyChoices, so only the shape the style binds to matters.
public sealed record SelectableChoice(string Value, string Label, bool IsEnabled = true);
