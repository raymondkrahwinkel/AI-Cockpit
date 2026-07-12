using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the command palette (#: command palette): a search box over every app action and plugin command, each
/// with its keyboard shortcut, VS-Code style. Type to filter, Enter/click to run. The chosen command's action
/// is exposed on <see cref="Chosen"/> and run by the host <em>after</em> the palette closes, so a command that
/// opens another dialog isn't stacked underneath this one.
/// </summary>
public partial class CommandPaletteDialogViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PaletteCommand> _all;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteCommand? _selected;

    public ObservableCollection<PaletteCommand> Visible { get; } = [];

    /// <summary>The action of the command the operator picked, or null if they cancelled — run by the caller once the dialog has closed.</summary>
    public Action? Chosen { get; private set; }

    public event Action? CloseRequested;

    // Design-time constructor for the previewer.
    public CommandPaletteDialogViewModel()
        : this([])
    {
    }

    public CommandPaletteDialogViewModel(IReadOnlyList<PaletteCommand> commands)
    {
        _all = commands;
        _ApplyFilter();
    }

    partial void OnQueryChanged(string value) => _ApplyFilter();

    private void _ApplyFilter()
    {
        var query = Query?.Trim();
        Visible.Clear();
        foreach (var command in _all)
        {
            if (string.IsNullOrEmpty(query) || command.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Visible.Add(command);
            }
        }

        Selected = Visible.Count > 0 ? Visible[0] : null;
    }

    /// <summary>Moves the selection up/down (from the search box's arrow keys), clamped to the list.</summary>
    public void Move(int delta)
    {
        if (Visible.Count == 0)
        {
            return;
        }

        var index = Selected is null ? 0 : Visible.IndexOf(Selected);
        Selected = Visible[Math.Clamp(index + delta, 0, Visible.Count - 1)];
    }

    [RelayCommand]
    private void Run(PaletteCommand? command)
    {
        var target = command ?? Selected;
        if (target is not null)
        {
            Chosen = target.Invoke;
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
