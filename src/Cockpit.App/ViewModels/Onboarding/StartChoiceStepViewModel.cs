using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the wizard's "How do you want to start?" step (AC-1504), slot 10: the host's own choices first, then whatever
// routes plugins registered. The picked route's content hangs under the choices, and whether it answers the
// work-kind question is what the shell reads to strike that step through.
public sealed partial class StartChoiceStepViewModel : ObservableObject
{
    private readonly IReadOnlyList<StartChoiceOptionViewModel> _builtIn;
    private readonly IFirstStartRouteRegistry? _routes;
    private readonly Dictionary<string, StartChoiceOptionViewModel> _routeOptions = [];

    // The registry is optional so the previewer and the screenshot scenes can stage the page without a container.
    public StartChoiceStepViewModel(IReadOnlyList<StartChoiceOptionViewModel> builtIn, IFirstStartRouteRegistry? routes = null)
    {
        _builtIn = builtIn;
        _routes = routes;
        _selected = builtIn[0];
        _selected.IsSelected = true;
        _ReloadOptions();

        if (routes is not null)
        {
            // A plugin registers from its own thread and can arrive after the wizard is already open.
            routes.Changed += (_, _) => Dispatcher.UIThread.Post(_ReloadOptions);
        }
    }

    public ObservableCollection<StartChoiceOptionViewModel> Options { get; } = [];

    // Why the wizard opened here, e.g. a --provision that failed (AC-1505); empty when nothing needs saying.
    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    private StartChoiceOptionViewModel _selected;

    public Control? SelectedContent => Selected.Content;

    public bool AnswersWorkKind => Selected.AnswersWorkKind;


    // Raised when the picked choice changes whether the work-kind step has anything left to ask.
    public event EventHandler? AnswersChanged;

    // Back to "Start fresh": a wizard opened again must not carry the last run's route into a step bar it did not pick.
    public void Reset() => Choose(_builtIn[0]);

    [RelayCommand]
    private void Choose(StartChoiceOptionViewModel option)
    {
        if (option == Selected)
        {
            return;
        }

        Selected.IsSelected = false;
        Selected = option;
        option.IsSelected = true;
        OnPropertyChanged(nameof(SelectedContent));
        OnPropertyChanged(nameof(AnswersWorkKind));
        AnswersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void _ReloadOptions()
    {
        Options.Clear();
        foreach (var option in _builtIn)
        {
            Options.Add(option);
        }

        foreach (var (route, pluginName) in _routes?.Routes ?? [])
        {
            Options.Add(_Wrap(route, pluginName));
        }
    }

    // One option per route for the life of the step: a reload must hand back the one already picked, not a copy.
    private StartChoiceOptionViewModel _Wrap(FirstStartRoute route, string pluginName)
    {
        if (!_routeOptions.TryGetValue(route.Id, out var option))
        {
            option = new StartChoiceOptionViewModel(
                route.Id, route.IconKind ?? MaterialIconKind.PuzzleOutline, route.Title, route.Description,
                route.CreateContent, pluginName, route.AnswersWorkKind);
            _routeOptions[route.Id] = option;
        }

        return option;
    }
}
