using Avalonia;
using Avalonia.Controls;
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
    private readonly BrowserModel _model;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly TextBox _address;
    private readonly MaterialIcon _lock = new() { Kind = MaterialIconKind.Lock, Width = 13, Height = 13, Margin = new Thickness(8, 0, 4, 0) };
    private readonly Border _codecBar;
    private readonly ContentControl _page = new();
    private readonly Button _placement;
    private BrowserTab? _watched;

    public BrowserShell(BrowserModel model, Action<string> openExternally)
    {
        _model = model;
        OpenExternally = openExternally;

        _back = _IconButton(MaterialIconKind.ArrowLeft, "Back", () => _model.Active?.View.GoBack());
        _forward = _IconButton(MaterialIconKind.ArrowRight, "Forward", () => _model.Active?.View.GoForward());
        _reload = _IconButton(MaterialIconKind.Refresh, "Reload", () => _model.Active?.View.Reload());
        _address = new TextBox
        {
            Watermark = "Search or enter an address",
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

        var addressField = new Border
        {
            CornerRadius = new CornerRadius(14),
            Height = 28,
            Child = new DockPanel { Children = { _Docked(_lock, Dock.Left), _address } },
        }.Themed(Border.BackgroundProperty, "CockpitInsetBgBrush");

        _placement = new Button { Classes = { "Compact", "Subtle" }, VerticalAlignment = VerticalAlignment.Center };
        _placement.Click += (_, _) => PlacementRequested?.Invoke();

        var more = _IconButton(MaterialIconKind.DotsHorizontal, "More", () => { });
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                _MenuItem("Open in system browser", () => _OpenActiveExternally()),
                _MenuItem("New tab", () => _model.NewTab()),
            },
        };

        var profile = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Children =
            {
                _Pill("● Raymond", active: true),
                _Pill("◆ Agents", active: false),
            },
        };
        ToolTip.SetTip(profile.Children[1], "Agent profiles arrive with AC-1713");

        var newTab = _IconButton(MaterialIconKind.Plus, "New tab", () => _model.NewTab());
        var tabRow = new DockPanel
        {
            Margin = new Thickness(8, 6, 8, 0),
            Height = 32,
            Children =
            {
                _Docked(profile, Dock.Left),
                _Docked(more, Dock.Right),
                _Docked(_placement, Dock.Right),
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
                addressField,
            },
        };

        _codecBar = _BuildCodecBar();

        var header = new StackPanel { Children = { tabRow, addressRow } }.Themed(StackPanel.BackgroundProperty, "CockpitSecondaryBgBrush");
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
        _model.Engine.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(_OnEngineChanged);
        SetPlacement("Dock in rail", MaterialIconKind.DockRight);
        _Refresh();
    }

    public Action<string> OpenExternally { get; }

    public event Action? PlacementRequested;

    public void SetPlacement(string label, MaterialIconKind icon) =>
        _placement.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { new MaterialIcon { Kind = icon, Width = 14, Height = 14 }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
        };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = _model.Engine.EnsureStartedAsync();
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
        _address.IsEnabled = ready && tab is not null;
        if (tab is not null && _address.IsFocused == false)
        {
            _address.Text = tab.Address == BrowserModel.StartAddress ? string.Empty : tab.Address;
        }

        _lock.Kind = tab?.Address.StartsWith("https://", StringComparison.Ordinal) ?? false ? MaterialIconKind.Lock : MaterialIconKind.Web;
        _codecBar.IsVisible = tab?.CodecUnsupported ?? false;
        _page.Content = ready && tab is not null ? tab.View : EngineStatusView.For(_model.Engine);
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
            Width = 200,
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

    private static Control _Pill(string text, bool active)
    {
        var pill = new Border
        {
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(10),
            IsEnabled = active,
            Child = new TextBlock { Text = text, FontSize = 12 }.Themed(TextBlock.ForegroundProperty, active ? "CockpitTextPrimaryBrush" : "CockpitTextFaintBrush"),
        };
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
