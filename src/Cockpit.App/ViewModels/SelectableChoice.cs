namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in a generic option dropdown (a plugin launch option or a live control): the <see cref="Value"/> the
/// provider gets back and the <see cref="Label"/> the operator reads. Equal when the provider supplied no label, so
/// an unlabelled option renders exactly as before. The combo binds its selection to <see cref="Value"/>
/// (<c>SelectedValueBinding</c>) and shows <see cref="Label"/> (<c>DisplayMemberBinding</c>), so the value round-trips
/// unchanged while the label is only ever display.
/// </summary>
public sealed record SelectableChoice(string Value, string Label);
