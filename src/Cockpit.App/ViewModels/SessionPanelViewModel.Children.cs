using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public abstract partial class SessionPanelViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingChild))]
    [NotifyPropertyChangedFor(nameof(CanReceiveOperatorInput))]
    private ChildTranscriptViewModel? _childTranscript;
    [ObservableProperty] private bool _isBackgroundWorkOpen;
    public bool IsReadingChild => ChildTranscript is not null;
    public bool HasChildAgents => ChildAgents.Count > 0;
    public bool HasBackgroundWorkEntries => HasBackgroundTasks || HasChildAgents;
    public bool ShowsLegacySubAgents => !HasChildAgents && HasBackgroundSubAgents;
    public virtual ObservableCollection<BackgroundTaskViewModel> BackgroundSubAgents { get; } = [];
    public virtual ObservableCollection<BackgroundTaskViewModel> BackgroundShells { get; } = [];
    public virtual ObservableCollection<BackgroundTaskViewModel> BackgroundOtherTasks { get; } = [];
    public virtual bool HasBackgroundSubAgents => BackgroundSubAgents.Count > 0;
    public virtual bool HasBackgroundShells => BackgroundShells.Count > 0;
    public virtual bool HasBackgroundOtherTasks => BackgroundOtherTasks.Count > 0;
    public virtual bool HasBackgroundTasks => BackgroundTaskCount > 0;
    public virtual int BackgroundTaskCount => ChildAgents.Count(child => child.Reference.State == "running");
    public virtual string BackgroundTaskSummary => BackgroundTaskCount switch
    {
        0 => "nothing",
        1 => "1 sub-agent",
        _ => $"{BackgroundTaskCount} sub-agents",
    };

    public void OpenChild(ChildAgentViewModel child)
    {
        if (!ReferenceEquals(child.Owner, this) || !ChildAgents.Contains(child))
        {
            return;
        }
        ReturnToParent();
        IsBackgroundWorkOpen = false;
        ChildTranscript = new ChildTranscriptViewModel(child, ReadChildAsync);
        ChildTranscript.LoadMoreCommand.Execute(null);
    }

    protected virtual Task<ChildTranscriptPage> ReadChildAsync(BackgroundTask reference, string? cursor, CancellationToken cancellationToken) =>
        Task.FromResult(ChildTranscriptPage.Unavailable("owner has no child reader"));

    internal void UpdateChildAgents(IReadOnlyList<BackgroundTask> children)
    {
        foreach (var removed in ChildAgents.Where(row => !children.Any(child => (child.AgentId ?? child.TaskId) == (row.Reference.AgentId ?? row.Reference.TaskId))).ToArray())
        {
            ChildAgents.Remove(removed);
            if (ReferenceEquals(ChildTranscript?.Child, removed))
            {
                ReturnToParent();
            }
        }
        foreach (var child in children)
        {
            var row = ChildAgents.FirstOrDefault(row => (row.Reference.AgentId ?? row.Reference.TaskId) == (child.AgentId ?? child.TaskId));
            if (row is null)
            {
                ChildAgents.Add(new ChildAgentViewModel(this, child));
            }
            else if (!ReferenceEquals(row.Reference, child))
            {
                if ((row.Reference.RunId != child.RunId || row.Reference.SourceRevision != child.SourceRevision) && ReferenceEquals(ChildTranscript?.Child, row))
                {
                    ReturnToParent();
                }
                row.Update(child);
            }
        }
        OnPropertyChanged(nameof(HasChildAgents));
        OnPropertyChanged(nameof(HasBackgroundWorkEntries));
        OnPropertyChanged(nameof(ShowsLegacySubAgents));
        OnPropertyChanged(nameof(BackgroundTaskCount));
        OnPropertyChanged(nameof(BackgroundTaskSummary));
        OnPropertyChanged(nameof(HasBackgroundTasks));
    }

    [RelayCommand]
    private void ReturnToParent()
    {
        ChildTranscript?.Dispose();
        ChildTranscript = null;
    }
}
