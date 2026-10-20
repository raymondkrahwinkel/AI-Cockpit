using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One project on the Overview: its live sessions with their state, what waits on the operator, the latest word.
public sealed record ProjectOverviewCard(string ProjectId, string Name, int Waiting, IReadOnlyList<string> Sessions, string? Latest)
{
    public bool HasWaiting => Waiting > 0;

    public bool HasSessions => Sessions.Count > 0;

    public string SessionsLabel => Sessions.Count switch
    {
        0 => "No sessions running",
        1 => "1 session",
        var count => $"{count} sessions",
    };

    public bool HasLatest => Latest is { Length: > 0 };
}

// Assistant threads: the Overview per project (voorstel § 2, § 4.9) and the composer's To: (mockup v2).
public sealed partial class AssistantChatViewModel
{
    // The longest stretch of a row the Overview quotes as the latest word from a project.
    private const int OverviewLatestLength = 140;

    public ObservableCollection<ProjectOverviewCard> OverviewCards { get; } = [];

    public bool HasOverviewCards => OverviewCards.Count > 0;

    // Rebuilt when the page opens and when its counts move, never per streamed row: a page nobody looks at costs nothing.
    private void _RebuildOverview()
    {
        OverviewCards.Clear();
        foreach (var item in ThreadItems.Where(item => item.Kind == AssistantThreadItemKind.Project))
        {
            var sessions = LiveSessions
                .Where(pane => pane.ProjectId == item.ProjectId)
                .Select(pane => string.IsNullOrEmpty(pane.SessionStatusLabel) ? pane.Title : $"{pane.Title} · {pane.SessionStatusLabel}")
                .ToList();
            var latest = Session?.Transcript.LastOrDefault(row =>
                (row.StartsTurn || row.IsAssistantMarkdown) && row.IsInThread(item.ProjectId) && row.Text.Trim().Length > 0);

            OverviewCards.Add(new ProjectOverviewCard(item.ProjectId!, item.Name, item.WaitingCount, sessions, _OneLine(latest?.Text)));
        }

        OnPropertyChanged(nameof(HasOverviewCards));
    }

    private static string? _OneLine(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var line = string.Join(' ', text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length > OverviewLatestLength ? line[..OverviewLatestLength] + "…" : line;
    }

    // --- To: in the composer --------------------------------------------------------------------------------------

    private readonly AssistantThreadItemViewModel _autoTarget = new(AssistantThreadItemKind.All, "Auto (the assistant picks)");

    // Auto, General and the projects: where what is typed or said now goes. It follows the open thread, and picking
    // one here sends the next prompts elsewhere without leaving the thread on screen.
    public ObservableCollection<AssistantThreadItemViewModel> ComposerTargets { get; } = [];

    [ObservableProperty]
    private AssistantThreadItemViewModel? _selectedComposerTarget;

    partial void OnSelectedComposerTargetChanged(AssistantThreadItemViewModel? value)
    {
        if (value is not null && Session is { } session)
        {
            session.ComposerThread = value.Kind == AssistantThreadItemKind.All ? null : value.ThreadKey;
        }
    }

    private void _RebuildComposerTargets(IReadOnlyList<(string Id, string Name)> projects)
    {
        var targets = new List<AssistantThreadItemViewModel> { _autoTarget, _generalItem };
        targets.AddRange(ThreadItems.Where(item => item.Kind == AssistantThreadItemKind.Project));
        if (targets.SequenceEqual(ComposerTargets))
        {
            return;
        }

        var keep = SelectedComposerTarget;
        ComposerTargets.Clear();
        foreach (var target in targets)
        {
            ComposerTargets.Add(target);
        }

        SelectedComposerTarget = keep is not null && ComposerTargets.Contains(keep) ? keep : _ComposerTargetFor(SelectedThreadItem);
    }

    private void _FollowOpenThreadInTheComposer() => SelectedComposerTarget = _ComposerTargetFor(SelectedThreadItem);

    // A project's or General's thread sends there; All, Questions and Overview send on Auto.
    private AssistantThreadItemViewModel _ComposerTargetFor(AssistantThreadItemViewModel? thread) =>
        thread is { IsThread: true } && ComposerTargets.Contains(thread) ? thread : _autoTarget;
}
