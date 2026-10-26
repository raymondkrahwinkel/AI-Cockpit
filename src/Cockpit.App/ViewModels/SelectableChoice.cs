namespace Cockpit.App.ViewModels;

// The combo binds its selection to `Value` (`SelectedValueBinding`) and shows `Label` (`DisplayMemberBinding`), so the
// value round-trips unchanged while the label is only ever display. `IsEnabled` false is a choice a policy rules out (AC-1593).
public sealed record SelectableChoice(string Value, string Label, bool IsEnabled = true);
