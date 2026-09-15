using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels.Onboarding;

public sealed partial class WorkKindChoiceViewModel(PluginWorkKindOption option) : ObservableObject
{
    public PluginWorkKindOption Option { get; } = option;

    public string Label => Option.Label;

    public string Description => Option.Description;

    [ObservableProperty]
    private bool _isSelected;
}
