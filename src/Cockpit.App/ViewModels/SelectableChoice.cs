namespace Cockpit.App.ViewModels;

// One entry in a generic option dropdown (a plugin launch option or a live control): the `Value` the
// provider gets back and the `Label` the operator reads. Equal when the provider supplied no label, so
// an unlabelled option renders exactly as before. The combo binds its selection to `Value`
// (`SelectedValueBinding`) and shows `Label` (`DisplayMemberBinding`), so the value round-trips
// unchanged while the label is only ever display.
public sealed record SelectableChoice(string Value, string Label);
