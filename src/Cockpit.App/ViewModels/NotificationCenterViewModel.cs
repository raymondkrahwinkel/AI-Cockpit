using System.ComponentModel;
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

// AC-1802: every toast of this session, fed by `ToastHostViewModel.Add`, behind the bell and the Notifications panel.
public sealed partial class NotificationCenterViewModel : ObservableObject
{
    public const string DockPanelId = "cockpit.notifications";
    public const string AppSource = "Cockpit";
    public const string AllSources = "All sources";
    private const int MaxItems = 200;

    private readonly List<NotificationItemViewModel> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNeedsYouFilter), nameof(IsUnreadFilter), nameof(IsAllFilter))]
    private NotificationFilter _filter = NotificationFilter.NeedsYou;

    [ObservableProperty]
    private string _sourceFilter = AllSources;

    // Newest first.
    public IReadOnlyList<NotificationItemViewModel> Items => _items;

    public IReadOnlyList<NotificationItemViewModel> VisibleItems { get; private set; } = [];

    public bool HasVisibleItems => VisibleItems.Count > 0;

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
    internal NotificationItemViewModel Post(ToastViewModel toast, string? source, string? key)
    {
        if (key is not null && _items.FindIndex(item => item.Key == key) is var index and >= 0)
        {
            _items[index].PropertyChanged -= _OnItemChanged;
            _items.RemoveAt(index);
        }

        var item = new NotificationItemViewModel(toast, string.IsNullOrWhiteSpace(source) ? AppSource : source, key, DateTimeOffset.Now, _Dismiss);
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
                NotificationFilter.NeedsYou => item.NeedsYou,
                NotificationFilter.Unread => !item.IsRead,
                _ => true,
            })];

        OnPropertyChanged(nameof(NeedsYouCount));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasNeedsYou));
        OnPropertyChanged(nameof(ShowsUnreadOnly));
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasVisibleItems));
    }
}

// One toast kept: its buttons are the toast's own, so a click here runs the same action and closes the toast too.
public sealed partial class NotificationItemViewModel : ObservableObject
{
    private readonly Action<NotificationItemViewModel> _dismiss;

    [ObservableProperty]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsYou), nameof(IsHandled), nameof(HandledReceipt))]
    private string? _handledBy;

    internal NotificationItemViewModel(ToastViewModel toast, string source, string? key, DateTimeOffset time, Action<NotificationItemViewModel> dismiss)
    {
        Message = toast.Message;
        Severity = toast.Severity;
        BrushKey = toast.BrushKey;
        // The toast's ✕ glyph for an error would read as this row's Dismiss.
        Glyph = toast.Severity is ToastSeverity.Error ? MaterialIconKind.AlertCircleOutline : toast.Glyph;
        Actions = toast.Actions;
        Source = source;
        Key = key;
        Time = time;
        _dismiss = dismiss;
    }

    public string Message { get; }

    public ToastSeverity Severity { get; }

    public string BrushKey { get; }

    public IReadOnlyList<ToastActionViewModel> Actions { get; }

    public string Source { get; }

    public string? Key { get; }

    public DateTimeOffset Time { get; }

    public string TimeLabel => Time.ToString("HH:mm");

    public bool IsHandled => HandledBy is not null;

    public bool NeedsYou => Actions.Count > 0 && !IsHandled;

    public string? HandledReceipt => HandledBy is { } label ? $"You chose: {label}" : null;

    public MaterialIconKind Glyph { get; }

    internal void MarkHandled(string actionLabel)
    {
        HandledBy = actionLabel;
        IsRead = true;
    }

    [RelayCommand]
    private void MarkRead() => IsRead = true;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}
