using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// One `AutoCompleteBox` worth of state for a single identifier a `ProjectPluginFieldViewModel` field is linked
// to (AC-884) — the single-value logic that field used to hold directly, one instance per row instead of shared.
//
// `Text` and `Identifier` are separate because the operator picks by name and the plugin queries by identifier:
// "AI-Cockpit — AC" is what is read, `AC` is what is kept. They only agree for a value typed by hand, which is
// exactly what a repository nobody granted read access to needs.
public partial class ProjectPluginFieldRowViewModel : ViewModelBase
{
    private readonly ProjectPluginFieldViewModel _field;

    internal ProjectPluginFieldRowViewModel(ProjectPluginFieldViewModel field, string identifier)
    {
        _field = field;
        _identifier = identifier;

        // Until the options arrive the identifier is all there is to show. A saved link must be visible the moment
        // the editor opens — a box that is blank while a list loads reads as "not linked", and an operator who saves
        // in that moment would make it true.
        _text = identifier;
    }

    // The choices the field's plugin supplied, shared by every one of the field's rows.
    public ObservableCollection<ProjectFieldOption> Options => _field.Options;

    public string? Placeholder => _field.Placeholder;

    // What is in the box — an option's display text once one is picked, the identifier itself otherwise.
    [ObservableProperty]
    private string _text;

    // What this row contributes to the field's stored value: the picked option's identifier, or whatever the
    // operator typed.
    [ObservableProperty]
    private string _identifier;

    // Whether this row's own remove control is live (AC-884, set by the field): the first row always stays, so a
    // field with one identifier has nothing to remove it *to* — its button space stays reserved but blank rather
    // than the row beside it losing its own alignment.
    [ObservableProperty]
    private bool _canRemove;

    partial void OnTextChanged(string value)
    {
        Identifier = Options.FirstOrDefault(option => string.Equals(option.Display, value, StringComparison.Ordinal))?.Value
            ?? value;
        _field.RecomputeValue();
    }

    internal void ShowUnderItsDisplayName()
    {
        if (Options.FirstOrDefault(option => string.Equals(option.Value, Identifier, StringComparison.Ordinal)) is { } match)
        {
            Text = match.Display;
        }
    }
}
