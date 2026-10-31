using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

public partial class EditableProfileViewModel
{
    [ObservableProperty]
    private string _localReasoningEffort = string.Empty;

    private string? _LocalReasoningEffort() => string.IsNullOrWhiteSpace(LocalReasoningEffort) ? null : LocalReasoningEffort.Trim();
}
