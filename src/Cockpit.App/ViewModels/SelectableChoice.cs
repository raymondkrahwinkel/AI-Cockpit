namespace Cockpit.App.ViewModels;

// The combo binds its selection to `Value` (`SelectedValueBinding`) and shows `Label` (`DisplayMemberBinding`), so the
// value round-trips unchanged while the label is only ever display.
public sealed record SelectableChoice(string Value, string Label);
