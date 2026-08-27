using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// One row per identifier rather than one box holding several comma-separated (AC-884) (AC-317).
public partial class ProjectPluginFieldViewModel : ViewModelBase
{
    private readonly ProjectFieldRegistration _registration;

    public ProjectPluginFieldViewModel(ProjectFieldRegistration registration, string? value)
    {
        _registration = registration;

        var identifiers = ProjectLinkValues.Split(value);
        foreach (var identifier in identifiers.Count > 0 ? identifiers : [string.Empty])
        {
            Rows.Add(new ProjectPluginFieldRowViewModel(this, identifier));
        }

        _value = ProjectLinkValues.Join(identifiers);
        _RefreshCanRemove();
    }

    public string Key => _registration.Key;

    public string Title => _registration.Title;

    public string? Hint => _registration.Hint;

    public string? Placeholder => _registration.Placeholder;

    // Whether this field's editor shows the add/remove row chrome. False for a field like GitHub's repository,
    // which stays the single bare box it always was (AC-884 non-goal).
    public bool AllowsMultiple => _registration.AllowsMultiple;

    // What gets stored on the project: every row's resolved identifier, comma-joined (AC-884). Recomputed by a
    // row whenever its own text changes, or by adding/removing a row — never written to directly.
    [ObservableProperty]
    private string _value;

    // One `AutoCompleteBox` worth of state per identifier. Always at least one, even for a field with nothing
    // linked yet, so the editor always has a row to type into.
    public ObservableCollection<ProjectPluginFieldRowViewModel> Rows { get; } = [];

    // The choices the plugin supplied, shared by every row — empty until they have loaded (or when it had none to offer).
    public ObservableCollection<ProjectFieldOption> Options { get; } = [];

    // Whether the choices are still coming — the row stays usable while they are, so the dialog never waits on a network call.
    [ObservableProperty]
    private bool _isLoadingOptions;

    // Why the choices could not be fetched, shown under the box. Null when they arrived, including when there were none.
    [ObservableProperty]
    private string? _loadError;

    // Fills `Options` from the plugin, and shows each row's saved identifier under its proper name once the list
    // can say what that name is. Never throws: a tracker that is unreachable costs this field its list, not the editor.
    public async Task LoadOptionsAsync(CancellationToken cancellationToken = default)
    {
        IsLoadingOptions = true;
        LoadError = null;

        try
        {
            // On a worker, not merely awaited: everything a plugin's fetch does before its own first await runs on
            // whichever thread called it, and for the GitHub field that is a process spawn — the editor would stutter
            // on opening. The continuation comes back to the UI thread, which is where Options may be added to.
            foreach (var option in await Task.Run(() => _registration.LoadOptionsAsync(cancellationToken), cancellationToken))
            {
                Options.Add(option);
            }

            foreach (var row in Rows)
            {
                row.ShowUnderItsDisplayName();
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the list was still coming; there is nobody left to tell.
        }
        catch (Exception exception)
        {
            // Said plainly rather than swallowed: "no options" and "the fetch failed" mean different things to
            // someone deciding whether their project is linked to the right place.
            LoadError = exception.Message;
        }
        finally
        {
            IsLoadingOptions = false;
        }
    }

    // Every row's resolved identifier, blanks dropped and rejoined (AC-884) — called by a row on every edit and
    // by the add/remove commands below, so `Value` never lags what is actually on screen.
    internal void RecomputeValue() =>
        Value = ProjectLinkValues.Join(Rows.Select(row => row.Identifier).Where(identifier => identifier.Length > 0));

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(new ProjectPluginFieldRowViewModel(this, string.Empty));
        _RefreshCanRemove();
    }

    // A no-op on the first row (Raymond, AC-884 review) — enforced here, not only via the button's disabled state,
    // so nothing but blanking its text can ever leave a field with zero rows. That first row is how a single
    // identifier reads as unlinked (AC-884 acceptance criterion 1), the same as it always was.
    [RelayCommand]
    private void RemoveRow(ProjectPluginFieldRowViewModel row)
    {
        if (Rows.IndexOf(row) == 0)
        {
            return;
        }

        Rows.Remove(row);
        RecomputeValue();
        _RefreshCanRemove();
    }

    // The first row never gets a remove control (Raymond, AC-884 review) — re-run after every add/remove, since
    // removing row 2 makes row 3 the new second.
    private void _RefreshCanRemove()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].CanRemove = i > 0;
        }
    }
}
