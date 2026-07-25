using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

/// <summary>
/// The MCP-server checklist as one control, shared by the profile editor, the New-session dialog and the project
/// editor (AC-140). The three used to carry their own copy of the same rows, which is how the project editor ended
/// up listing servers the other two had long stopped offering.
/// <para>
/// Collapsed by default behind a live "N of M selected" count: the list runs to a dozen rows in a dialog that is
/// about something else, and someone who is not a developer should not have to read past it to reach Save.
/// </para>
/// </summary>
public partial class McpServerChecklist : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ServersProperty =
        AvaloniaProperty.Register<McpServerChecklist, IEnumerable?>(nameof(Servers));

    /// <summary>What the header calls the list, before the count — "MCP servers" everywhere so far.</summary>
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<McpServerChecklist, string>(nameof(Header), "MCP servers");

    /// <summary>A line above the rows saying what ticking one means here; blank leaves it out.</summary>
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<McpServerChecklist, string?>(nameof(Hint));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<McpServerChecklist, bool>(nameof(IsExpanded));

    /// <summary>The pre-flight tool-token total for the ticked servers (AC-134); shown under the rows when <see cref="ShowTokenSummary"/>.</summary>
    public static readonly StyledProperty<string?> TokenSummaryProperty =
        AvaloniaProperty.Register<McpServerChecklist, string?>(nameof(TokenSummary));

    public static readonly StyledProperty<bool> ShowTokenSummaryProperty =
        AvaloniaProperty.Register<McpServerChecklist, bool>(nameof(ShowTokenSummary));

    public static readonly StyledProperty<ICommand?> RefreshCommandProperty =
        AvaloniaProperty.Register<McpServerChecklist, ICommand?>(nameof(RefreshCommand));

    public static readonly DirectProperty<McpServerChecklist, string> SummaryTextProperty =
        AvaloniaProperty.RegisterDirect<McpServerChecklist, string>(nameof(SummaryText), control => control.SummaryText);

    private string _summaryText = string.Empty;

    /// <summary>The rows this checklist shows — <see cref="McpServerSelectionItemViewModel"/>s owned by whichever dialog is hosting it.</summary>
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

    /// <summary>The header line: the name and how many of the rows are ticked, so a collapsed list still says what it holds.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetAndRaise(SummaryTextProperty, ref _summaryText, value);
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

    private void _RefreshSummary()
    {
        var servers = Servers?.OfType<McpServerSelectionItemViewModel>().ToList() ?? [];
        var selected = servers.Count(server => server.IsEnabledForSession);
        SummaryText = servers.Count == 0
            ? Header
            : $"{Header} · {selected} of {servers.Count} selected";
    }
}
