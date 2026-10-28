using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.Docking;
using Material.Icons;

namespace Cockpit.App.ViewModels;

public enum NotificationFilter
{
    NeedsYou,
    Unread,
    All,
}

// AC-1853 (mockup screens 04 and 09), in the order the panel shows them.
public enum NotificationGroup
{
    LastSession,
    Today,
    Earlier,
}

// AC-1802: every toast of this session, fed by `ToastHostViewModel.Add`, behind the bell and the Notifications panel.
public sealed partial class NotificationCenterViewModel : ObservableObject
{
    public const string DockPanelId = "cockpit.notifications";
    public const string AppSource = "Cockpit";
    public const string AllSources = "All sources";
    private const int MaxItems = 200;

    private readonly List<NotificationItemViewModel> _items = [];
    private string? _keptPath;
    private string? _keptJson;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNeedsYouFilter), nameof(IsUnreadFilter), nameof(IsAllFilter))]
    private NotificationFilter _filter = NotificationFilter.NeedsYou;

    [ObservableProperty]
    private string _sourceFilter = AllSources;

    // Newest first.
    public IReadOnlyList<NotificationItemViewModel> Items => _items;

    public IReadOnlyList<NotificationItemViewModel> VisibleItems { get; private set; } = [];

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public IReadOnlyList<NotificationGroupViewModel> VisibleGroups { get; private set; } = [];

    // AC-1853: the icon a plugin already registered (its side-menu button), by plugin id; the cockpit sets it.
    internal Func<string, MaterialIconKind?> PluginIcon { get; set; } = static _ => null;

    public IReadOnlyList<string> Sources { get; private set; } = [AllSources];

    public int NeedsYouCount { get; private set; }

    public int UnreadCount { get; private set; }

    public bool HasNeedsYou => NeedsYouCount > 0;

    // The accent count on the bell: unread, but only while nothing needs you (that count is the amber one).
    public bool ShowsUnreadOnly => NeedsYouCount == 0 && UnreadCount > 0;

    public bool IsNeedsYouFilter => Filter == NotificationFilter.NeedsYou;

    public bool IsUnreadFilter => Filter == NotificationFilter.Unread;

    public bool IsAllFilter => Filter == NotificationFilter.All;

    public NotificationCenterViewModel()
    {
        Badge.Activated += (_, _) => Filter = NotificationFilter.NeedsYou;
    }

    // The rail tab's count: what needs you, like the Assistant tab's open questions.
    public DockPanelBadge Badge { get; } = new();

    // With a key, the item it replaces goes: a source that says the same thing again (an update check) stays one item.
    internal NotificationItemViewModel Post(ToastViewModel toast, string? source, string? key, string? sourceId = null)
    {
        if (key is not null && _items.FindIndex(item => item.Key == key && item.SourceId == sourceId) is var index and >= 0)
        {
            _items[index].PropertyChanged -= _OnItemChanged;
            _items.RemoveAt(index);
        }

        var item = new NotificationItemViewModel(toast, string.IsNullOrWhiteSpace(source) ? AppSource : source, key, sourceId, DateTimeOffset.Now, _Dismiss)
        {
            SourceIcon = _SourceIcon(source, sourceId),
        };
        item.PropertyChanged += _OnItemChanged;
        _items.Insert(0, item);

        if (_items.Count > MaxItems)
        {
            _items[^1].PropertyChanged -= _OnItemChanged;
            _items.RemoveAt(_items.Count - 1);
        }

        _Refresh();
        return item;
    }

    // AC-1804: from here on, what still needs you from a plugin source is written to `path`; the caller picks it.
    internal void KeepAt(string path)
    {
        _keptPath = path;
        _Keep();
    }

    internal static IReadOnlyList<KeptNotification> ReadKept(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<KeptNotification>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    [RelayCommand]
    private void SetFilter(string filter) => Filter = Enum.Parse<NotificationFilter>(filter);

    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var item in _items)
        {
            item.IsRead = true;
        }
    }

    partial void OnFilterChanged(NotificationFilter value) => _Refresh();

    // The source picker hands back null while its list is swapped; that is not the operator picking nothing.
    partial void OnSourceFilterChanged(string value)
    {
        if (value is null)
        {
            SourceFilter = AllSources;
            return;
        }

        _Refresh();
    }

    private void _Dismiss(NotificationItemViewModel item)
    {
        item.PropertyChanged -= _OnItemChanged;
        _items.Remove(item);
        _Refresh();
    }

    private void _ClearRead(IReadOnlyList<NotificationItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.PropertyChanged -= _OnItemChanged;
            _items.Remove(item);
        }

        _Refresh();
    }

    // A plugin's own icon, else the puzzle every plugin launcher falls back to; the app's own sources a fixed one.
    private MaterialIconKind _SourceIcon(string? source, string? sourceId) => sourceId is not null
        ? PluginIcon(sourceId) ?? MaterialIconKind.PuzzleOutline
        : source == "Settings sync" ? MaterialIconKind.Sync : MaterialIconKind.ApplicationCogOutline;

    private static NotificationGroup _GroupOf(NotificationItemViewModel item) =>
        item.FromLastSession ? NotificationGroup.LastSession
        : item.Time.ToLocalTime().Date == DateTime.Today ? NotificationGroup.Today
        : NotificationGroup.Earlier;

    private void _OnItemChanged(object? sender, PropertyChangedEventArgs e) => _Refresh();

    private void _Refresh()
    {
        NeedsYouCount = _items.Count(item => item.NeedsYou);
        UnreadCount = _items.Count(item => !item.IsRead);
        Badge.Count = NeedsYouCount;
        IReadOnlyList<string> sources = [AllSources, .. _items.Select(item => item.Source).Distinct().Order()];
        if (!sources.SequenceEqual(Sources))
        {
            Sources = sources;
            OnPropertyChanged(nameof(Sources));
        }

        VisibleItems = [.. _items.Where(item =>
            (SourceFilter == AllSources || item.Source == SourceFilter)
            && Filter switch
            {
                // A restart's answer stays in view too: kept, then resolved elsewhere (mockup screen 09).
                NotificationFilter.NeedsYou => item.NeedsYou || item is { FromLastSession: true, IsHandled: true },
                NotificationFilter.Unread => !item.IsRead,
                _ => true,
            })];
        VisibleGroups = [.. VisibleItems.GroupBy(_GroupOf).OrderBy(group => group.Key)
            .Select(group => new NotificationGroupViewModel(group.Key, [.. group], _ClearRead))];

        OnPropertyChanged(nameof(NeedsYouCount));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasNeedsYou));
        OnPropertyChanged(nameof(ShowsUnreadOnly));
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(VisibleGroups));
        _Keep();
    }

    private void _Keep()
    {
        if (_keptPath is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(_items
            .Where(item => item is { NeedsYou: true, SourceId: not null, Key: not null })
            .Select(item => new KeptNotification(item.SourceId!, item.Source, item.Key!, item.Message, item.Severity, item.Title, item.Time)));
        if (json == _keptJson)
        {
            return;
        }

        try
        {
            File.WriteAllText(_keptPath, json);
            _keptJson = json;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // ponytail: a failed write only loses what a restart would bring back; the next change tries again.
        }
    }
}

// What a restart brings back for its source to re-check (AC-1804).
// A file from before AC-1853 has no Title or Time.
internal sealed record KeptNotification(string SourceId, string Source, string Key, string Message, ToastSeverity Severity, string? Title = null, DateTimeOffset? Time = null);

// One heading in the panel with its rows; Clear read takes only the read ones of this group.
public sealed partial class NotificationGroupViewModel(NotificationGroup group, IReadOnlyList<NotificationItemViewModel> items, Action<IReadOnlyList<NotificationItemViewModel>> clearRead)
{
    public NotificationGroup Group { get; } = group;

    public string Title => Group switch
    {
        NotificationGroup.LastSession => "Still open from your last session",
        NotificationGroup.Today => "Today",
        _ => "Earlier",
    };

    public bool IsLastSession => Group == NotificationGroup.LastSession;

    public IReadOnlyList<NotificationItemViewModel> Items { get; } = items;

    public bool HasRead => Items.Any(item => item.IsRead);

    [RelayCommand]
    private void ClearRead() => clearRead(Items);
}

// One toast kept: its buttons are the toast's own, so a click here runs the same action and closes the toast too.
public sealed partial class NotificationItemViewModel : ObservableObject
{
    private readonly Action<NotificationItemViewModel> _dismiss;

    [ObservableProperty]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsYou), nameof(IsHandled))]
    private string? _handledReceipt;

    // AC-1853: kept over a restart (AC-1804), so it sits under "Still open from your last session".
    [ObservableProperty]
    private bool _fromLastSession;

    internal NotificationItemViewModel(ToastViewModel toast, string source, string? key, string? sourceId, DateTimeOffset time, Action<NotificationItemViewModel> dismiss)
    {
        Message = toast.Message;
        Title = toast.HasTitle ? toast.Title : null;
        Severity = toast.Severity;
        BrushKey = toast.BrushKey;
        // The toast's ✕ glyph for an error would read as this row's Dismiss.
        Glyph = toast.Severity is ToastSeverity.Error ? MaterialIconKind.AlertCircleOutline : toast.Glyph;
        Actions = toast.Actions;
        Source = source;
        Key = key;
        SourceId = sourceId;
        Time = time;
        _dismiss = dismiss;
    }

    public string Message { get; }

    public string? Title { get; }

    public bool HasTitle => Title is not null;

    public ToastSeverity Severity { get; }

    public string BrushKey { get; }

    public MaterialIconKind SourceIcon { get; init; } = MaterialIconKind.ApplicationCogOutline;

    public IReadOnlyList<ToastActionViewModel> Actions { get; }

    public string Source { get; }

    public string? Key { get; }

    // The plugin that can re-check it after a restart (AC-1804); null for the app's own.
    public string? SourceId { get; }

    public DateTimeOffset Time { get; private set; }

    // A day other than today reads as "Mon 17:40" (mockup screen 09).
    public string TimeLabel => Time.ToLocalTime().Date == DateTime.Today
        ? Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : Time.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);

    public bool IsHandled => HandledReceipt is not null;

    public bool NeedsYou => Actions.Count > 0 && !IsHandled;

    public MaterialIconKind Glyph { get; }

    internal void MarkHandled(string actionLabel)
    {
        HandledReceipt = $"You chose: {actionLabel}";
        IsRead = true;
    }

    // AC-1804: kept over a restart, and its source said it no longer stands.
    internal void MarkResolved() => HandledReceipt = "Resolved meanwhile";

    internal void KeptFrom(DateTimeOffset? time)
    {
        Time = time ?? Time;
        FromLastSession = true;
    }

    [RelayCommand]
    private void MarkRead() => IsRead = true;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}
