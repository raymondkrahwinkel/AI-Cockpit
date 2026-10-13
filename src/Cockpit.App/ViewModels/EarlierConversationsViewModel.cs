using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1532: the Earlier conversations sheet — the index searched by words, project and date, and Continue brings one back.
public sealed partial class EarlierConversationsViewModel : ObservableObject
{
    private readonly CockpitViewModel _cockpit;
    private CancellationTokenSource? _searching;

    internal EarlierConversationsViewModel(CockpitViewModel cockpit, IConversationIndex? index)
    {
        _cockpit = cockpit;
        Index = index;
        _projectFilter = ProjectFilters[0];
        _dateFilter = DateFilters[0];
    }

    // Settable so a scene draws the sheet over fixture rows instead of the operator's own index.
    internal IConversationIndex? Index { get; set; }

    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private EarlierProjectFilter? _projectFilter;

    [ObservableProperty]
    private EarlierDateFilter _dateFilter;

    [ObservableProperty]
    private EarlierConversationRow? _selected;

    [ObservableProperty]
    private string _foundLabel = string.Empty;

    public ObservableCollection<EarlierProjectFilter> ProjectFilters { get; } = [EarlierProjectFilter.All];

    public IReadOnlyList<EarlierDateFilter> DateFilters { get; } =
    [
        new("Any date", EarlierDate.Any),
        new("Today", EarlierDate.Today),
        new("This week", EarlierDate.ThisWeek),
        new("This month", EarlierDate.ThisMonth),
        new("Earlier", EarlierDate.Earlier),
    ];

    public ObservableCollection<EarlierConversationRow> Rows { get; } = [];

    [RelayCommand]
    private Task OpenAsync()
    {
        var picked = ProjectFilter;
        ProjectFilters.Clear();
        ProjectFilters.Add(EarlierProjectFilter.All);
        foreach (var project in _cockpit.Projects.Projects)
        {
            ProjectFilters.Add(new EarlierProjectFilter(project.Name, project.Id));
        }

        ProjectFilters.Add(EarlierProjectFilter.None);
        ProjectFilter = ProjectFilters.FirstOrDefault(filter => filter == picked) ?? EarlierProjectFilter.All;
        IsOpen = true;
        return RefreshAsync();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    // The sheet closes only once the conversation stands; a refusal stays on the row as its reason.
    [RelayCommand]
    private async Task ContinueAsync(EarlierConversationRow? row)
    {
        if ((row ?? Selected) is not { CanContinue: true } chosen)
        {
            return;
        }

        var result = await _cockpit.ReopenConversationAsync(chosen.Record);
        if (result.Reopened)
        {
            IsOpen = false;
        }
        else if (Rows.IndexOf(chosen) is >= 0 and var at)
        {
            Rows[at] = chosen with { Refusal = result.Reason ?? "it could not be started." };
        }
    }

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    partial void OnProjectFilterChanged(EarlierProjectFilter? value) => _ = RefreshAsync();

    partial void OnDateFilterChanged(EarlierDateFilter value) => _ = RefreshAsync();

    // Each change starts over; a search a later keystroke overtook drops its rows instead of drawing them.
    internal async Task RefreshAsync()
    {
        if (!IsOpen || Index is null)
        {
            return;
        }

        _searching?.Cancel();
        var searching = _searching = new CancellationTokenSource();
        var project = ProjectFilter ?? EarlierProjectFilter.All;
        var now = Now();
        var today = new DateTimeOffset(now.Date, now.Offset);
        var (from, to) = DateFilter.Date switch
        {
            EarlierDate.Today => (today, (DateTimeOffset?)null),
            EarlierDate.ThisWeek => (today.AddDays(-6), null),
            EarlierDate.ThisMonth => (today.AddDays(-29), null),
            EarlierDate.Earlier => ((DateTimeOffset?)null, today.AddDays(-29)),
            _ => ((DateTimeOffset?)null, (DateTimeOffset?)null),
        };

        IReadOnlyList<ConversationSearchResult> found;
        try
        {
            found = await Index.SearchAsync(SearchText, project.ProjectId, from, to, searching.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var rows = new List<EarlierConversationRow>();
        foreach (var result in found.Where(result => !project.WithoutProject || result.Record.ProjectId is null))
        {
            rows.Add(await _RowAsync(result, today));
        }

        if (searching.IsCancellationRequested)
        {
            return;
        }

        Rows.Clear();
        string? group = null;
        foreach (var row in rows.OrderByDescending(row => row.Record.LastActivityAt))
        {
            Rows.Add(row.Group == group ? row : row with { GroupHeader = row.Group.ToUpperInvariant() });
            group = row.Group;
        }

        FoundLabel = Rows.Count == 1 ? "1 conversation found" : $"{Rows.Count} conversations found";
        Selected = Rows.FirstOrDefault();
    }

    private async Task<EarlierConversationRow> _RowAsync(ConversationSearchResult result, DateTimeOffset today)
    {
        var record = result.Record;
        var at = record.LastActivityAt.ToOffset(today.Offset);
        var needle = SearchText.Trim();
        var place = record.ProjectId is { } id && _cockpit.Projects.Projects.FirstOrDefault(project => project.Id == id) is { } project
            ? project.Name
            : record.WorkingDirectory is { Length: > 0 } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : null;
        var fragment = needle.Length == 0 || result.Fragment is null || result.Fragment == record.Title
            ? null
            : HighlightedText.Of(result.Fragment.ReplaceLineEndings(" "), needle);
        return new EarlierConversationRow(
            record,
            HighlightedText.Of(record.Title, needle),
            fragment,
            record.IsAssistant ? null : record.ProviderId?.ToUpperInvariant(),
            record.IsAssistant ? null : place,
            at >= today ? "Today" : at >= today.AddDays(-6) ? "This week" : "Earlier",
            at >= today ? $"today {at:HH:mm}"
                : at >= today.AddDays(-1) ? $"yesterday {at:HH:mm}"
                : at.ToString("ddd d MMM", CultureInfo.InvariantCulture),
            SessionPanelViewModel.BrushKeyOf(Enum.TryParse<SessionStatus>(record.LastStatus, out var status) ? status : SessionStatus.Idle),
            await _cockpit.ConversationRefusalAsync(record));
    }
}

public enum EarlierDate
{
    Any,
    Today,
    ThisWeek,
    ThisMonth,
    Earlier,
}

public sealed record EarlierDateFilter(string Label, EarlierDate Date)
{
    public override string ToString() => Label;
}

// "No project" is a filter the index has no argument for, so it carries its own flag rather than a made-up id.
public sealed record EarlierProjectFilter(string Label, string? ProjectId, bool WithoutProject = false)
{
    public static readonly EarlierProjectFilter All = new("All projects", null);

    public static readonly EarlierProjectFilter None = new("No project", null, WithoutProject: true);

    public override string ToString() => Label;
}

public sealed record EarlierConversationRow(
    ConversationRecord Record,
    HighlightedText Title,
    HighlightedText? Fragment,
    string? ProviderBadge,
    string? Place,
    string Group,
    string DateLabel,
    string StatusBrushKey,
    string? Refusal)
{
    public string? GroupHeader { get; init; }

    public bool CanContinue => Refusal is null;

    public string? ContinueTip => Refusal is null ? null : $"This conversation can't be continued: {Refusal}";

    public string? Profile => Record.ProfileId;
}

// The first hit of the search term in a line, split so the view can mark it.
public sealed record HighlightedText(string Before, string Match, string After)
{
    public static HighlightedText Of(string text, string needle)
    {
        var hit = needle.Length == 0 ? -1 : text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        return hit < 0
            ? new HighlightedText(text, string.Empty, string.Empty)
            : new HighlightedText(text[..hit], text.Substring(hit, needle.Length), text[(hit + needle.Length)..]);
    }
}
