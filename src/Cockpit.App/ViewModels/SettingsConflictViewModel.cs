using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewModels;

// AC-1510: the settings that changed both here and on another machine since they last agreed, one choice per setting.
// A row left unanswered stays a choice for later; nothing is decided for the operator.
public sealed partial class SettingsConflictViewModel : ViewModelBase
{
    // Design-time constructor for the previewer and the render scene.
    public SettingsConflictViewModel()
        : this(
        [
            new SettingsConflict("Layout.ThemeMode", "\"Dark\"", "\"Light\""),
            new SettingsConflict("Terminal.FontSize", "13", "15"),
            new SettingsConflict("Profiles.Work", """{"Label":"Work","Defaults":{"Model":"opus"}}""", null),
        ])
    {
    }

    public SettingsConflictViewModel(IReadOnlyList<SettingsConflict> conflicts)
    {
        Rows = [.. conflicts.Select(conflict => new SettingsConflictRowViewModel(conflict))];
        foreach (var row in Rows)
        {
            row.PropertyChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    // The answers per setting key: null keeps this machine's value, otherwise the carrier id is selected.
    public event Action<IReadOnlyDictionary<string, string?>?>? CloseRequested;

    public IReadOnlyList<SettingsConflictRowViewModel> Rows { get; }

    private bool CanApply => Rows.Any(row => row.HasChoice);

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(null);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => CloseRequested?.Invoke(
        Rows.Where(row => row.HasChoice).ToDictionary(row => row.Key, row => row.SelectedCarrier, StringComparer.Ordinal));
}
