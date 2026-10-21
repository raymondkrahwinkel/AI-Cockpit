using System.Collections.ObjectModel;

namespace Cockpit.App.ViewModels;

// One project on the Overview: its live sessions with their state, what waits on the operator, the latest word.
public sealed record ProjectOverviewCard(string ProjectId, string Name, int Waiting, IReadOnlyList<string> Sessions, string? Latest, string? LogoPath = null)
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

// Assistant threads: the Overview per project (voorstel § 2, § 4.9).
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

            OverviewCards.Add(new ProjectOverviewCard(item.ProjectId!, item.Name, item.WaitingCount, sessions, _OneLine(latest?.Text), item.LogoPath));
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
}
