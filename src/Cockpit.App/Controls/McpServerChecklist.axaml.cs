using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1013: The MCP-server checklist as one control shared by the profile editor, New-session dialog and
// project editor (AC-140) — the three used to keep their own copy, which is how the project editor ended up
// listing stale servers. Collapsed by default behind a live summary line so it doesn't block Save.
public partial class McpServerChecklist : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ServersProperty =
        AvaloniaProperty.Register<McpServerChecklist, IEnumerable?>(nameof(Servers));

    // What the header calls the list, before the count — "MCP servers" everywhere so far.
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<McpServerChecklist, string>(nameof(Header), "MCP servers");

    // A line above the rows saying what ticking one means here; blank leaves it out.
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<McpServerChecklist, string?>(nameof(Hint));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<McpServerChecklist, bool>(nameof(IsExpanded));

    // The pre-flight tool-token total for the ticked servers (AC-134); shown under the rows when `ShowTokenSummary`.
    public static readonly StyledProperty<string?> TokenSummaryProperty =
        AvaloniaProperty.Register<McpServerChecklist, string?>(nameof(TokenSummary));

    public static readonly StyledProperty<bool> ShowTokenSummaryProperty =
        AvaloniaProperty.Register<McpServerChecklist, bool>(nameof(ShowTokenSummary));

    public static readonly StyledProperty<ICommand?> RefreshCommandProperty =
        AvaloniaProperty.Register<McpServerChecklist, ICommand?>(nameof(RefreshCommand));

    public static readonly DirectProperty<McpServerChecklist, string> SummaryTextProperty =
        AvaloniaProperty.RegisterDirect<McpServerChecklist, string>(nameof(SummaryText), control => control.SummaryText);

    // AC-248: names the hosting dialog found no server for on this machine. The control words it, so the project
    // editor and the New-session dialog cannot end up saying two different things about one state.
    public static readonly StyledProperty<IEnumerable?> UnavailableServersProperty =
        AvaloniaProperty.Register<McpServerChecklist, IEnumerable?>(nameof(UnavailableServers));

    public static readonly DirectProperty<McpServerChecklist, string?> UnavailableTextProperty =
        AvaloniaProperty.RegisterDirect<McpServerChecklist, string?>(nameof(UnavailableText), control => control.UnavailableText);

    private string _summaryText = string.Empty;

    private string? _unavailableText;

    // The rows this checklist shows — `McpServerSelectionItemViewModel`s owned by whichever dialog is hosting it.
    public IEnumerable? Servers
    {
        get => GetValue(ServersProperty);
        set => SetValue(ServersProperty, value);
    }

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public string? TokenSummary
    {
        get => GetValue(TokenSummaryProperty);
        set => SetValue(TokenSummaryProperty, value);
    }

    public bool ShowTokenSummary
    {
        get => GetValue(ShowTokenSummaryProperty);
        set => SetValue(ShowTokenSummaryProperty, value);
    }

    public ICommand? RefreshCommand
    {
        get => GetValue(RefreshCommandProperty);
        set => SetValue(RefreshCommandProperty, value);
    }

    // The names this checklist has no row for, because this machine offers no such server.
    public IEnumerable? UnavailableServers
    {
        get => GetValue(UnavailableServersProperty);
        set => SetValue(UnavailableServersProperty, value);
    }

    // The header line: the name and how many of the rows are ticked, so a collapsed list still says what it holds.
    public string SummaryText
    {
        get => _summaryText;
        private set => SetAndRaise(SummaryTextProperty, ref _summaryText, value);
    }

    // The warning under the header; null when nothing is missing, which is also what hides the line.
    public string? UnavailableText
    {
        get => _unavailableText;
        private set => SetAndRaise(UnavailableTextProperty, ref _unavailableText, value);
    }

    public McpServerChecklist()
    {
        InitializeComponent();
        _RefreshSummary();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ServersProperty)
        {
            _Unsubscribe(change.OldValue as IEnumerable);
            _Subscribe(change.NewValue as IEnumerable);
            _RefreshSummary();
        }
        else if (change.Property == HeaderProperty)
        {
            _RefreshSummary();
        }
        else if (change.Property == UnavailableServersProperty)
        {
            _RefreshUnavailable();
        }
    }

    // AC-248: names them rather than counting them — there is no row to expand to, so the name is the only way to
    // know which server to go and set up.
    private void _RefreshUnavailable()
    {
        var missing = UnavailableServers?.OfType<string>().ToList() ?? [];
        UnavailableText = missing.Count switch
        {
            0 => null,
            1 => $"Not on this machine: {missing[0]} — a session starts without it.",
            _ => $"Not on this machine: {string.Join(", ", missing)} — a session starts without them.",
        };
    }

    // The count has to follow the boxes as they are ticked, so the collapsed header keeps telling the truth: that
    // means listening to the collection and to every row in it, and re-listening when rows are rebuilt (which the
    // New-session dialog does on every project switch).
    private void _Subscribe(IEnumerable? servers)
    {
        if (servers is null)
        {
            return;
        }

        if (servers is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += _OnServersChanged;
        }

        foreach (var server in servers.OfType<McpServerSelectionItemViewModel>())
        {
            server.PropertyChanged += _OnServerPropertyChanged;
        }
    }

    private void _Unsubscribe(IEnumerable? servers)
    {
        if (servers is null)
        {
            return;
        }

        if (servers is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged -= _OnServersChanged;
        }

        foreach (var server in servers.OfType<McpServerSelectionItemViewModel>())
        {
            server.PropertyChanged -= _OnServerPropertyChanged;
        }
    }

    private void _OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var removed in e.OldItems?.OfType<McpServerSelectionItemViewModel>() ?? [])
        {
            removed.PropertyChanged -= _OnServerPropertyChanged;
        }

        foreach (var added in e.NewItems?.OfType<McpServerSelectionItemViewModel>() ?? [])
        {
            added.PropertyChanged += _OnServerPropertyChanged;
        }

        // A reset carries no items, so the rows have to be re-read rather than diffed from the arguments.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _Subscribe(Servers);
        }

        _RefreshSummary();
    }

    private void _OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpServerSelectionItemViewModel.IsEnabledForSession))
        {
            _RefreshSummary();
        }
    }

    // Naming what is off, not only how many are on: a count is what let a switched-off Depot sit unnoticed on a
    // project for months, since the one line a collapsed list shows was true and said nothing. Past a handful the
    // names stop being readable at a glance, and the count is the better answer again.
    private const int NamesShownWhenOff = 3;

    private void _RefreshSummary()
    {
        var servers = Servers?.OfType<McpServerSelectionItemViewModel>().ToList() ?? [];
        var off = servers.Where(server => !server.IsEnabledForSession).Select(server => server.Name).ToList();
        SummaryText = servers switch
        {
            { Count: 0 } => Header,
            _ when off.Count == 0 => $"{Header} · all {servers.Count} selected",
            _ when off.Count <= NamesShownWhenOff => $"{Header} · {string.Join(", ", off)} off",
            _ => $"{Header} · {servers.Count - off.Count} of {servers.Count} selected",
        };
    }
}
