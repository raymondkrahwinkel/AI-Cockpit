using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

public partial class RolePickerViewModel : ViewModelBase
{
    private int _generation;
    private BrainChoice _selected = BrainPickerViewModel.None;

    [ObservableProperty]
    private IReadOnlyList<BrainChoice> _choices = [BrainPickerViewModel.None];

    [ObservableProperty]
    private bool _isAvailable;

    public BrainChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selected, value);
            }
        }
    }

    public void Restore(string? role)
    {
        var selected = string.IsNullOrWhiteSpace(role) ? BrainPickerViewModel.None : new BrainChoice(role, role, null);
        Choices = selected.Reference is null ? [BrainPickerViewModel.None] : [BrainPickerViewModel.None, selected];
        _selected = selected;
        OnPropertyChanged(nameof(Selected));
    }

    public async Task LoadAsync(SessionRoleResolver? resolver, Project? project, SessionProfile? profile, Func<SessionProfile?, Task<RoleListResult>>? remoteList = null)
    {
        var generation = ++_generation;
        RoleListResult? result;
        try
        {
            result = remoteList is not null ? await remoteList(profile).ConfigureAwait(true)
                : resolver is null ? null : await resolver.ListAsync(project, profile).ConfigureAwait(true);
        }
        catch (Exception)
        {
            result = new(RoleSourceOutcome.Unavailable, []);
        }
        if (generation != _generation)
        {
            return;
        }
        var listed = result?.Roles.Select(role => new BrainChoice(role.Name, role.Name, role.Description)).ToArray() ?? [];
        var selected = _selected.Reference is null ? BrainPickerViewModel.None
            : listed.FirstOrDefault(role => role.Reference == _selected.Reference) ?? _selected;
        Choices = selected.Reference is null || listed.Contains(selected)
            ? [BrainPickerViewModel.None, .. listed] : [BrainPickerViewModel.None, selected, .. listed];
        _selected = selected;
        IsAvailable = listed.Length > 0 || selected.Reference is not null;
        OnPropertyChanged(nameof(Selected));
    }
}
