using CommunityToolkit.Mvvm.Input;
using Cockpit.Infrastructure.Plugins;

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

    // The answers per setting key, true to keep this machine's value; null when the operator chose later.
    public event Action<IReadOnlyDictionary<string, bool>?>? CloseRequested;

    public IReadOnlyList<SettingsConflictRowViewModel> Rows { get; }

    private bool CanApply => Rows.Any(row => row.KeepMine || row.TakeTheirs);

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(null);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => CloseRequested?.Invoke(
        Rows.Where(row => row.KeepMine || row.TakeTheirs).ToDictionary(row => row.Key, row => row.KeepMine, StringComparer.Ordinal));
}
