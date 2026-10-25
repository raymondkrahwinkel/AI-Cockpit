using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Browser.UI.Engine;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Browser.UI;

// The browser as Raymond sees it (AC-1711, mockup screens 1-3): profile switch and tabs, address bar, the codec bar
// and the page. One instance per process; BrowserHome hangs it in the tab or the rail. With no engine it shows why.
internal sealed class BrowserShell : UserControl
{
    private static readonly Color AgentPurple = Color.Parse("#A67CD8");

    private readonly BrowserModel _model;
    private readonly AgentTabsModel _agents;
    private readonly AgentScreen _agentScreen;
    private readonly Border _agentBar = new() { IsVisible = false };
    private readonly StackPanel _profile = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Avalonia.Threading.DispatcherTimer _visibility = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Border _agentLabel = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _newTab;
    private bool _agentSide;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly TextBox _address;
    private readonly MaterialIcon _lock = new() { Kind = MaterialIconKind.Lock, Width = 13, Height = 13, Margin = new Thickness(8, 0, 4, 0) };
    private readonly TextBlock _addressShown = new() { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _codecBar;
    private readonly ContentControl _page = new();
    private readonly Button _placement;
    private BrowserTab? _watched;
    private bool _compact;

    public BrowserShell(BrowserModel model, AgentTabsModel agents, Action<string> openExternally)
    {
        _model = model;
        _agents = agents;
        _agentScreen = new AgentScreen(agents);
        OpenExternally = openExternally;

        _back = _IconButton(MaterialIconKind.ArrowLeft, "Back", () => _model.Active?.View.GoBack());
        _forward = _IconButton(MaterialIconKind.ArrowRight, "Forward", () => _model.Active?.View.GoForward());
        _reload = _IconButton(MaterialIconKind.Refresh, "Reload", () => _model.Active?.View.Reload());
        _address = new TextBox
        {
            PlaceholderText = "Search or enter an address",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 4),
        };
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _model.Active is { } tab)
            {
                tab.Navigate(BrowserAddress.Resolve(_address.Text ?? string.Empty));
                tab.View.Focus();
                e.Handled = true;
            }
        };
        _lock.Themed(MaterialIcon.ForegroundProperty, "CockpitTextSecondaryBrush");
        _address.GotFocus += (_, _) => _Refresh();
        _address.LostFocus += (_, _) => _Refresh();

        var addressField = new Border
        {
            CornerRadius = new CornerRadius(14),
            Height = 28,
            Child = new DockPanel { Children = { _Docked(_lock, Dock.Left), new Grid { Children = { _address, _addressShown } } } },
        }.Themed(Border.BackgroundProperty, "CockpitInsetBgBrush");

        _placement = _IconButton(MaterialIconKind.KeyboardReturn, "Back to tab", () => PlacementRequested?.Invoke());
        _placement.Margin = new Thickness(6, 0, 0, 0);

        var more = _IconButton(MaterialIconKind.DotsHorizontal, "More", () => { });
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                _MenuItem("Open in system browser", () => _OpenActiveExternally()),
                _MenuItem("New tab", () => _model.NewTab()),
            },
        };

        var newTab = _newTab = _IconButton(MaterialIconKind.Plus, "New tab", () => _model.NewTab());
        var tabRow = new DockPanel
        {
            Margin = new Thickness(8, 6, 8, 0),
            Height = 32,
            Children =
            {
                _Docked(_profile, Dock.Left),
                _Docked(more, Dock.Right),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabStrip, newTab } },
                },
            },
        };

        var addressRow = new DockPanel
        {
            Margin = new Thickness(8, 4, 8, 6),
            Children =
            {
                _Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { _back, _forward, _reload } }, Dock.Left),
                _Docked(_placement, Dock.Right),
                _Docked(_agentLabel, Dock.Right),
                addressField,
            },
        };

        _agentLabel.Padding = new Thickness(8, 2);
        _agentLabel.CornerRadius = new CornerRadius(10);
        _agentLabel.Background = new SolidColorBrush(AgentPurple, 0.18);
        _agentLabel.Child = new TextBlock { Text = "◆ agent profile", FontSize = 11, Foreground = new SolidColorBrush(AgentPurple) };
        _codecBar = _BuildCodecBar();

        var header = new StackPanel { Children = { tabRow, _agentBar, addressRow } }.Themed(StackPanel.BackgroundProperty, "CockpitSecondaryBgBrush");
        var hairline = new Border { Height = 1 }.Themed(Border.BackgroundProperty, "CockpitHairlineBrush");

        Content = new DockPanel
        {
            Children =
            {
                _Docked(header, Dock.Top),
                _Docked(hairline, Dock.Top),
                _Docked(_codecBar, Dock.Top),
                _page,
            },
        };

        _model.Changed += _Refresh;
        _agents.Changed += _Refresh;
        _visibility.Tick += (_, _) => _UpdateViewing();
        _model.Engine.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(_OnEngineChanged);
        ShowBackToTab(false);
        _Refresh();
    }

    public Action<string> OpenExternally { get; }

    public BrowserModel Model => _model;

    public event Action? PlacementRequested;

    // Mockup screen 2: in the rail (and its own window) the way back sits at the end of the address bar.
    public void ShowBackToTab(bool visible) => _placement.IsVisible = visible;

    // Compact by width, not by place: narrow in the rail (mockup screen 2), full tabs in its own window (screen 3).
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 640;
        if (compact != _compact)
        {
            _compact = compact;
            _Refresh();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visibility.Start();
        _ = _model.Engine.EnsureStartedAsync();
    }

    // A panel that is collapsed or floated away keeps its shell alive; only a visible one is watched (AC-1713).
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _visibility.Stop();
        _UpdateViewing();
    }

    private void _OnEngineChanged()
    {
        if (_model.Engine.State == BrowserEngineState.Ready && _model.Tabs.Count == 0)
        {
            _model.NewTab();
            return;
        }

        _Refresh();
    }

    private void _Refresh()
    {
        _RebuildProfile();
        _agentBar.IsVisible = _agentSide;
        _agentLabel.IsVisible = _agentSide;
        _newTab.IsVisible = _agentSide == false;
        _UpdateViewing();
        if (_agentSide)
        {
            _RefreshAgents();
            return;
        }

        if (ReferenceEquals(_watched, _model.Active) == false)
        {
            if (_watched is not null)
            {
                _watched.Changed -= _Refresh;
            }

            _watched = _model.Active;
            if (_watched is not null)
            {
                _watched.Changed += _Refresh;
            }
        }

        _RebuildTabs();
        var tab = _model.Active;
        var ready = _model.Engine.State == BrowserEngineState.Ready;
        _back.IsEnabled = tab?.CanGoBack ?? false;
        _forward.IsEnabled = tab?.CanGoForward ?? false;
        _reload.IsEnabled = tab is not null;
        _address.IsReadOnly = (ready && tab is not null) == false;
        if (tab is not null && _address.IsFocused == false)
        {
            _address.Text = _Display(tab.Address);
        }

        _ShowAddress(_address.IsFocused ? string.Empty : _address.Text ?? string.Empty);

        _lock.Kind = tab?.Address.StartsWith("https://", StringComparison.Ordinal) ?? false ? MaterialIconKind.Lock : MaterialIconKind.Web;
        _codecBar.IsVisible = tab?.CodecUnsupported ?? false;
        _page.Content = ready && tab is not null ? tab.View : EngineStatusView.For(_model.Engine);
    }

    // The Agents side (AC-1713, mockup screen 5): the tabs of the agent browsers, read-only, with their live picture.
    private void _RefreshAgents()
    {
        _RebuildAgentTabs();
        var tab = _agents.Active;
        _back.IsEnabled = false;
        _forward.IsEnabled = false;
        _reload.IsEnabled = false;
        _address.IsReadOnly = true;
        _address.Text = tab is null ? string.Empty : _Display(tab.Url);
        _ShowAddress(_address.Text);
        _lock.Kind = tab?.Url.StartsWith("https://", StringComparison.Ordinal) ?? false ? MaterialIconKind.Lock : MaterialIconKind.Web;
        _codecBar.IsVisible = false;
        _BuildAgentBar(tab);
        _page.Content = tab is null ? _AgentsEmpty() : _agentScreen;
        _agentScreen.InvalidateVisual();
    }

    private void _UpdateViewing() => _agents.SetViewing(_agentSide && IsEffectivelyVisible);

    private void _RebuildProfile()
    {
        _profile.Children.Clear();
        _profile.Children.Add(_Pill("● Raymond", _agentSide == false, () => _SwitchSide(agents: false)));
        var count = _agents.Tabs.Count;
        _profile.Children.Add(_Pill(count > 0 ? $"◆ Agents {count}" : "◆ Agents", _agentSide, () => _SwitchSide(agents: true)));
    }

    // A headless scene opens on the Agents side.
    internal void ShowAgents() => _SwitchSide(agents: true);

    private void _SwitchSide(bool agents)
    {
        _agentSide = agents;
        _agents.Show(agents);
        _Refresh();
    }

    private void _RebuildAgentTabs()
    {
        _tabStrip.Children.Clear();
        foreach (var tab in _agents.Tabs)
        {
            _tabStrip.Children.Add(_AgentChip(tab));
        }
    }

    private Control _AgentChip(AgentTab tab)
    {
        var active = ReferenceEquals(tab, _agents.Active);
        var badge = new Border
        {
            Padding = new Thickness(7, 1),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(AgentPurple, 0.18),
            Child = new TextBlock { Text = tab.Badge, FontSize = 11, Foreground = new SolidColorBrush(AgentPurple), MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_Docked(badge, Dock.Right));
        row.Children.Add(new TextBlock
        {
            Text = tab.Title.Length == 0 ? tab.Url : tab.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        }.Themed(TextBlock.ForegroundProperty, active ? "CockpitTextPrimaryBrush" : "CockpitTextSecondaryBrush"));
        if (tab.Icon is not null || tab.FaviconUrl.Length > 0)
        {
            var icon = new Image { Source = tab.Icon, Width = 14, Height = 14 };
            row.Children.Insert(0, _Docked(icon, Dock.Left));
            if (tab.Icon is null)
            {
                _ = _LoadIcon(icon, tab.FaviconUrl);
            }
        }

        var chip = new Border
        {
            Width = _compact ? 170 : 240,
            Height = 28,
            Padding = new Thickness(6, 0, 4, 0),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            BorderBrush = new SolidColorBrush(AgentPurple, 0.6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };
        if (active)
        {
            chip.Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");
        }

        chip.PointerPressed += (_, _) => _agents.Activate(tab);
        ToolTip.SetTip(chip, tab.Session + ": " + tab.Title);
        return chip;
    }

    // Mockup screen 5: who drives this tab, with Neem over (Take over) and Stop agent.
    private void _BuildAgentBar(AgentTab? tab)
    {
        if (tab is null)
        {
            _agentBar.Child = null;
            return;
        }

        var drives = tab.OperatorDrives;
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        text.Inlines ??= [];
        text.Inlines.Add(new Run("Agent profile") { FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Run(drives ? "  ·  you are driving: the session's browser calls are refused until you give it back" : "  ·  live view of the agent Chromium  ·  no logins of Raymond  ·  driven by "));
        if (drives == false)
        {
            text.Inlines.Add(new Run(tab.Session) { FontWeight = FontWeight.SemiBold });
            text.Inlines.Add(new Run(" via Playwright MCP"));
        }

        var take = new Button { Content = drives ? "Give back" : "Take over", Classes = { "Compact", drives ? "Accent" : "Subtle" }, Margin = new Thickness(6, 0, 0, 0) };
        take.Click += async (_, _) => await (drives ? _agents.GiveBackAsync() : _agents.TakeOverAsync());
        var stop = new Button { Content = new TextBlock { Text = "Stop agent", Foreground = new SolidColorBrush(Color.Parse("#E5484D")) }, Classes = { "Compact", "Subtle" }, Margin = new Thickness(6, 0, 0, 0) };
        stop.Click += async (_, _) => await _agents.StopAsync();
        ToolTip.SetTip(stop, "Close this session's Chromium; its browser calls fail until it opens a new one");

        _agentBar.Background = new SolidColorBrush(AgentPurple, 0.12);
        _agentBar.Padding = new Thickness(12, 5);
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(AgentPurple), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _agentBar.Child = new DockPanel { Children = { _Docked(dot, Dock.Left), _Docked(stop, Dock.Right), _Docked(take, Dock.Right), text } };
    }

    private static async Task _LoadIcon(Image image, string url) => image.Source = await Favicons.GetAsync(url);

    private Control _AgentsEmpty()
    {
        var message = _agents.Error ?? "No agent has a browser open. A session that mounts Playwright MCP gets one, and its tabs show here.";
        return new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) }
            .Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush");
    }

    // Mockup screen 1: the host in bold, the rest faint, while the field is not being edited.
    private void _ShowAddress(string shown)
    {
        _addressShown.Inlines?.Clear();
        _addressShown.IsVisible = shown.Length > 0;
        if (shown.Length == 0)
        {
            _address.ClearValue(TextBox.ForegroundProperty);
            return;
        }

        _address.Foreground = Brushes.Transparent;
        var slash = shown.IndexOf('/');
        var host = slash < 0 ? shown : shown[..slash];
        _addressShown.Inlines ??= [];
        _addressShown.Inlines.Add(new Run(host) { FontWeight = FontWeight.SemiBold });
        if (slash >= 0)
        {
            var path = new Run(shown[slash..]);
            path.Bind(Run.ForegroundProperty, _addressShown.GetResourceObservable("CockpitTextFaintBrush"));
            _addressShown.Inlines.Add(path);
        }
    }

    // As the mockup shows it while the field is not being edited: no scheme, no "www.".
    private static string _Display(string address)
    {
        if (address == BrowserModel.StartAddress)
        {
            return string.Empty;
        }

        var shown = address.StartsWith("https://", StringComparison.Ordinal) ? address["https://".Length..]
            : address.StartsWith("http://", StringComparison.Ordinal) ? address["http://".Length..] : address;
        return shown.StartsWith("www.", StringComparison.Ordinal) ? shown["www.".Length..] : shown;
    }

    private void _RebuildTabs()
    {
        _tabStrip.Children.Clear();
        foreach (var tab in _model.Tabs)
        {
            _tabStrip.Children.Add(_TabChip(tab));
        }
    }

    private Control _TabChip(BrowserTab tab)
    {
        var active = ReferenceEquals(tab, _model.Active);
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

        if (tab.Favicon is { } favicon)
        {
            row.Children.Add(_Docked(new Image { Source = favicon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0) }, Dock.Left));
        }

        if (tab.IsAudible || tab.IsMuted)
        {
            var sound = _IconButton(tab.IsMuted ? MaterialIconKind.VolumeOff : MaterialIconKind.VolumeHigh, tab.IsMuted ? "Unmute this tab" : "Mute this tab", tab.ToggleMute, 13);
            row.Children.Add(_Docked(sound, Dock.Left));
        }

        var close = _IconButton(MaterialIconKind.Close, "Close tab", () => _model.Close(tab), 12);
        row.Children.Add(_Docked(close, Dock.Right));
        row.Children.Add(new TextBlock
        {
            Text = tab.IsLoading && tab.Title == "New tab" ? "Loading…" : tab.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        }.Themed(TextBlock.ForegroundProperty, active ? "CockpitTextPrimaryBrush" : "CockpitTextSecondaryBrush"));

        var chip = new Border
        {
            Width = _compact ? 140 : 200,
            Height = 28,
            Padding = new Thickness(6, 0, 2, 0),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        }.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush");
        if (active)
        {
            chip.Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");
        }

        chip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(chip).Properties.IsMiddleButtonPressed)
            {
                _model.Close(tab);
                return;
            }

            _model.Activate(tab);
        };
        ToolTip.SetTip(chip, tab.Title);
        return chip;
    }

    private Border _BuildCodecBar()
    {
        var open = new Button { Content = "Open in system browser", Classes = { "Compact", "Accent" }, Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => _OpenActiveExternally();
        var dismiss = _IconButton(MaterialIconKind.Close, "Dismiss", () =>
        {
            if (_model.Active is { } tab)
            {
                tab.CodecUnsupported = false;
                _Refresh();
            }
        }, 12);

        return new Border
        {
            Padding = new Thickness(12, 6),
            IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    _Docked(dismiss, Dock.Right),
                    _Docked(open, Dock.Right),
                    new TextBlock { Text = "This page asks for a video format the built-in browser does not have", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                },
            },
        }.Themed(Border.BackgroundProperty, "CockpitStatusWaitingChipTintBrush");
    }

    private void _OpenActiveExternally()
    {
        if (_model.Active is { } tab && Uri.TryCreate(tab.Address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            OpenExternally(uri.AbsoluteUri);
        }
    }

    private static Control _Pill(string text, bool active, Action onClick)
    {
        var pill = new Border
        {
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 12 }.Themed(TextBlock.ForegroundProperty, active ? "CockpitTextPrimaryBrush" : "CockpitTextFaintBrush"),
        };
        pill.PointerPressed += (_, _) => onClick();
        return active ? pill.Themed(Border.BackgroundProperty, "CockpitAccentSelectionBrush") : pill;
    }

    private static Button _IconButton(MaterialIconKind icon, string tip, Action onClick, double size = 16)
    {
        var button = new Button
        {
            Classes = { "Compact", "Ghost" },
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new MaterialIcon { Kind = icon, Width = size, Height = size },
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static MenuItem _MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static T _Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
