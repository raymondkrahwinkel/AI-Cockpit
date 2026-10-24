namespace Cockpit.App.ViewModels;

// AC-1593: Theme.axaml types a style on the host's SelectableChoice. The app compiles it inside Cockpit.App;
// HeadlessAvalonia parses it at runtime without that assembly, so the runtime XAML compiler needs this stand-in.
public sealed record SelectableChoice(string Value, string Label, bool IsEnabled = true);
