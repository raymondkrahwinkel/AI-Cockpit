using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

public partial class SessionViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingChild))]
    private ChildTranscriptViewModel? _childTranscript;
    public bool IsReadingChild => ChildTranscript is not null;
    protected override bool CanReceiveOperatorInput => !IsReadingChild;
    public bool HasChildAgents => ChildAgents.Count > 0;
    public bool HasBackgroundWorkEntries => HasBackgroundTasks || HasChildAgents;
    public bool ShowsLegacySubAgents => !HasChildAgents && HasBackgroundSubAgents;

    [ObservableProperty]
    private bool _isBackgroundWorkOpen;

    public void OpenChild(ChildAgentViewModel child)
    {
        if (!ReferenceEquals(child.Owner, this) || !ChildAgents.Contains(child))
        {
            return;
        }
        ReturnToParent();
        IsBackgroundWorkOpen = false;
        ChildTranscript = new ChildTranscriptViewModel(child, _control);
        ChildTranscript.LoadMoreCommand.Execute(null);
    }

    [RelayCommand]
    private void ReturnToParent()
    {
        ChildTranscript?.Dispose();
        ChildTranscript = null;
    }
}
