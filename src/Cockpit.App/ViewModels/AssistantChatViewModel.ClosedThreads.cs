using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// Assistant threads: closing a thread from the rail, and the folded "Closed (n)" at its foot to bring one back.
public sealed partial class AssistantChatViewModel
{
    public ObservableCollection<AssistantThreadItemViewModel> ClosedThreadItems { get; } = [];

    public bool HasClosedThreads => ClosedThreadItems.Count > 0;

    public string ClosedThreadsLabel => $"Closed ({ClosedThreadItems.Count})";

    // Folded by default: the closed threads are out of the way until someone wants one back.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClosedThreadsIconKind))]
    private bool _isClosedThreadsExpanded;

    public MaterialIconKind ClosedThreadsIconKind => IsClosedThreadsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    [RelayCommand]
    private void ToggleClosedThreads() => IsClosedThreadsExpanded = !IsClosedThreadsExpanded;

    // The icon rail has no room for the list, so its button opens the labelled overlay with the list folded out.
    [RelayCommand]
    private void ShowClosedThreads()
    {
        IsClosedThreadsExpanded = true;
        if (RailStand == AssistantRailStand.Icons)
        {
            IsRailOverlayOpen = true;
        }
    }

    // What a closed thread's row action says, and why it is not offered: General and a thread with something waiting.
    public string? WhyThreadCannotClose(AssistantThreadItemViewModel? item) =>
        item is { Kind: AssistantThreadItemKind.Project } ? Session?.WhyThreadCannotClose(item.ProjectId) : "Only a project's thread closes.";

    // The operator's own close, from the rail's row action, its context menu or Delete: their click is the decision.
    [RelayCommand]
    private void CloseThread(AssistantThreadItemViewModel? item)
    {
        if (item is not { Kind: AssistantThreadItemKind.Project, ProjectId: { } projectId } || Session is not { } session)
        {
            return;
        }

        if (session.CloseThread(projectId) is { Ok: false, Note: { } refusal })
        {
            MoveAnnouncement = refusal;
            return;
        }

        if (ReferenceEquals(SelectedThreadItem, item))
        {
            SelectedThreadItem = _allItem;
        }

        MoveAnnouncement = $"Closed the {item.Name} thread. It is under Closed at the foot of the list.";
    }

    // Brings a closed thread back and opens it.
    [RelayCommand]
    private void ReopenThread(AssistantThreadItemViewModel? item)
    {
        if (item?.ProjectId is not { } projectId || Session is not { } session)
        {
            return;
        }

        session.ReopenThread(projectId);
        IsRailOverlayOpen = false;
        SelectedThreadItem = ThreadItems.FirstOrDefault(open => open.ProjectId == projectId) ?? SelectedThreadItem;
        MoveAnnouncement = $"Reopened the {item.Name} thread.";
    }

    private void _RebuildClosedThreadItems()
    {
        var closed = (Session?.ClosedThreads ?? [])
            .Select(id => new AssistantThreadItemViewModel(AssistantThreadItemKind.Project, Session?.ProjectName(id) ?? _ProjectName(id) ?? id, id, _ProjectLogo(id)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (closed.Select(item => item.ProjectId).SequenceEqual(ClosedThreadItems.Select(item => item.ProjectId)))
        {
            return;
        }

        ClosedThreadItems.Clear();
        foreach (var item in closed)
        {
            ClosedThreadItems.Add(item);
        }

        OnPropertyChanged(nameof(HasClosedThreads));
        OnPropertyChanged(nameof(ClosedThreadsLabel));
    }
}
