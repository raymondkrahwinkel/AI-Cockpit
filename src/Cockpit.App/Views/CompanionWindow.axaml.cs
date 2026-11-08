using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Configuration;
using Cockpit.Core.Layout;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// Hosts the mini-tools plugins register via `ICockpitHost.AddCompanionTool` as a strip. Chrome copied from
// `VoiceOverlayWindow`, but interactive rather than a passive pill, so it skips that window's X11 click-through
// input shape (VoiceOverlayWindow._TryEnableClickThrough) — that would make every tool inside unclickable.
public partial class CompanionWindow : Window
{
    private const string BoundsKey = "companion";
    private const double PopOutWidth = 360;

    private readonly IWindowBoundsStore? _windowBoundsStore;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CompanionPopOutHost _popOuts;
    private readonly Dictionary<string, (CompanionStripItem Item, Control Control)> _controls = [];

    public CompanionWindow()
        : this(Program.Services?.GetService<IWindowBoundsStore>())
    {
    }

    // Test seam: lets a test control what the position-restore below reads, same shape as AssistantChatWindow's own.
    internal CompanionWindow(IWindowBoundsStore? windowBoundsStore)
    {
        _windowBoundsStore = windowBoundsStore;
        InitializeComponent();
        Title = $"{CockpitProduct.DisplayName} companion";
        _popOuts = new CompanionPopOutHost(this);

        Card.PointerPressed += _OnCardPressed;
        PositionChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _positionSaveTimer.Stop();
                _positionSaveTimer.Start();
            }
        };
        _positionSaveTimer.Tick += (_, _) =>
        {
            _positionSaveTimer.Stop();
            _ = SavePositionAsync();
        };
        Opened += (_, _) => KeepOnScreen();
        OrientationItem.Click += (_, _) => _Change(
            Orientation == CompanionOrientation.Horizontal ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal, AlwaysOnTop);
        TopmostItem.Click += (_, _) => _Change(Orientation, !AlwaysOnTop);
        HideItem.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        OrientationItem.Click += (_, _) => MoreButton.Flyout?.Hide();
        TopmostItem.Click += (_, _) => MoreButton.Flyout?.Hide();
        ApplyLayout(CompanionOrientation.Horizontal, alwaysOnTop: true);

        // Only the position is worth restoring: this window is SizeToContent, so a saved size would never apply.
        var saved = _windowBoundsStore?.LoadAsync(BoundsKey).GetAwaiter().GetResult();
        if (saved is not null && RestoredWindowBounds.IsOnAScreen(saved, Screens.All.Select(s => s.WorkingArea)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(saved.X, saved.Y);
        }
    }

    // AC-1522: every operator close (⋯ › Hide, Alt+F4, taskbar) is a request, so CompanionWindowPresenter.Hide stays
    // the one route that hides the window and persists the state.
    public event EventHandler? CloseRequested;

    // Raised when the operator picks another orientation or toggles always on top, so the presenter can persist it.
    public event EventHandler? LayoutChanged;

    public CompanionOrientation Orientation { get; private set; }

    public bool AlwaysOnTop => Topmost;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    public void ApplyLayout(CompanionOrientation orientation, bool alwaysOnTop)
    {
        var horizontal = orientation == CompanionOrientation.Horizontal;
        Orientation = orientation;
        Topmost = alwaysOnTop;
        TopmostIcon.Kind = alwaysOnTop ? MaterialIconKind.Check : MaterialIconKind.PinOutline;
        var layout = horizontal ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
        Strip.Orientation = layout;
        ToolsHost.Orientation = layout;
        GripIcon.Kind = horizontal ? MaterialIconKind.DragVertical : MaterialIconKind.DragHorizontal;
        OrientationLabel.Text = horizontal ? "Vertical strip" : "Horizontal strip";
        _popOuts.Orientation = orientation;
        _popOuts.CloseAll();
        foreach (var (_, control) in _controls.Values)
        {
            if (control is AssistantIndicator indicator)
            {
                indicator.Collapsed = !horizontal;
            }
        }

        // The grip is at the start in both orientations and stays put; only a strip that now runs off the screen moves.
        Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);
    }

    // Rebuilds the strip from the registry (CompanionWindowPresenter calls this on every registry change).
    // Replacing the whole list is simpler than diffing and companion tools change rarely — a plugin loading, not a
    // per-frame update.
    internal void SetTools(IReadOnlyList<CompanionStripItem> items)
    {
        _popOuts.CloseAll("companion.customize", "companion.unavailable");
        EmptyState.IsVisible = items.Count == 0;
        ToolsHost.IsVisible = items.Count > 0;
        ToolsHost.Children.Clear();
        _controls.Clear();
        foreach (var item in items)
        {
            Control control;
            if (item.Inline)
            {
                control = item.CreateView();
                if (control is AssistantIndicator indicator)
                {
                    indicator.Collapsed = Orientation == CompanionOrientation.Vertical;
                }
            }
            else
            {
                var button = new CompanionStripButton(item);
                button.Click += (_, _) => _TogglePopOut(item, button, showActivated: true);
                control = button;
            }

            _controls[item.Id] = (item, control);
            ToolsHost.Children.Add(control);
        }

        Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);
    }

    // A tool asked for its pop-out (ICompanionToolContext.RequestPopOut): open it without taking the focus.
    public void OpenPopOut(string toolId)
    {
        if (_controls.TryGetValue(toolId, out var entry) && !entry.Item.Inline && !_popOuts.IsOpen(toolId))
        {
            _TogglePopOut(entry.Item, entry.Control, showActivated: false);
        }
    }

    // The strip's card without its window, for a scene that lays it on a stand-in screen.
    internal Control TakeCard()
    {
        Content = null;

        return Card;
    }

    public void ClosePopOuts() => _popOuts.CloseAll();

    internal void ConfigureCustomization(CompanionCustomizationModel model, Func<string, Task> enable)
    {
        CustomizeItem.Click += (_, _) =>
        {
            MoreButton.Flyout?.Hide();
            _popOuts.Toggle("companion.customize", MoreButton,
                new CompanionCustomizePanel(model, enable, () => _popOuts.Close("companion.customize")), 400);
        };
    }

    internal bool ShowUnavailableNotice(string message)
    {
        const string key = "companion.unavailable";
        _popOuts.Close(key);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("CockpitTextPrimaryBrush"));
        var dismiss = new Button { Content = "Dismiss", Classes = { "Ghost", "Compact" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        dismiss.Click += (_, _) => _popOuts.Close(key);
        return _popOuts.Toggle(key, MoreButton, new StackPanel { Margin = new Thickness(16), Spacing = 8, Children = { text, dismiss } }, 320, showActivated: false);
    }

    private void _TogglePopOut(CompanionStripItem item, Control button, bool showActivated)
    {
        if (_popOuts.IsOpen(item.Id))
        {
            _popOuts.CloseAll();
            return;
        }

        var title = new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeight.Medium };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("CockpitTextPrimaryBrush"));
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { title, item.CreateView() } };
        _popOuts.Toggle(item.Id, button, content, PopOutWidth, showActivated);
    }

    private void _Change(CompanionOrientation orientation, bool alwaysOnTop)
    {
        ApplyLayout(orientation, alwaysOnTop);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // AC-241: let the window manager move it, including compositors that do not allow absolute positioning.
    // Buttons handle their own presses, so what reaches the card is the grip or an empty spot.
    private void _OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Card).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    // Pulls the strip back inside the work area when a restore, a new screen layout or a longer strip put it outside.
    internal void KeepOnScreen()
    {
        if (Bounds.Width <= 0 || Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * RenderScaling), (int)Math.Ceiling(Bounds.Height * RenderScaling));
        var clamped = ClampInto(screen.WorkingArea, new PixelRect(Position, size), (int)Math.Ceiling(Card.Margin.Left * RenderScaling));
        if (clamped != Position)
        {
            Position = clamped;
        }
    }

    // Moves the card (the window minus its shadow margin) inside the work area; the shadow may hang over the edge.
    internal static PixelPoint ClampInto(PixelRect workArea, PixelRect window, int shadow)
    {
        var minX = workArea.X - shadow;
        var minY = workArea.Y - shadow;
        var maxX = Math.Max(minX, workArea.Right + shadow - window.Width);
        var maxY = Math.Max(minY, workArea.Bottom + shadow - window.Height);

        return new PixelPoint(Math.Clamp(window.X, minX, maxX), Math.Clamp(window.Y, minY, maxY));
    }

    internal async Task SavePositionAsync()
    {
        _positionSaveTimer.Stop();
        if (_windowBoundsStore is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        try
        {
            await _windowBoundsStore.SaveAsync(BoundsKey, new WindowBounds(Position.X, Position.Y,
                (int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling), IsMaximized: false))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort, same as AssistantChatWindow's own position save.
        }
    }
}
