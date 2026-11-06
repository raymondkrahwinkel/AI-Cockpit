using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Configuration;
using Cockpit.Core.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// AC-237: hosts the mini-tools plugins register via `ICockpitHost.AddCompanionTool`. Chrome copied from
// `VoiceOverlayWindow`, but interactive rather than a passive pill, so it skips that window's X11 click-through
// input shape (VoiceOverlayWindow._TryEnableClickThrough) — that would make every tool inside unclickable.
public partial class CompanionWindow : Window
{
    private const string BoundsKey = "companion";

    private readonly IWindowBoundsStore? _windowBoundsStore;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

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

        HeaderBar.PointerPressed += _OnHeaderPressed;
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
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        // Only the position is worth restoring: this window is SizeToContent, so a saved width/height would
        // never apply the way AssistantChatWindow's HasUsableSize gate expects for its own resizable window.
        var saved = _windowBoundsStore?.LoadAsync(BoundsKey).GetAwaiter().GetResult();
        // AC-241: the header must remain reachable, not just the bottom of a tall tool list.
        if (saved is not null && RestoredWindowBounds.IsOnAScreen(saved with { Height = 32 }, Screens.All.Select(s => s.WorkingArea)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(saved.X, saved.Y);
        }
    }

    // AC-1522: every operator close (X button, Alt+F4, taskbar) is a request, so CompanionWindowPresenter.Hide stays
    // the one route that hides the window and persists the state.
    public event EventHandler? CloseRequested;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    // Rebuilds the hosted tools from the registry (CompanionWindowPresenter calls this on every registry change).
    // Replacing the whole list is simpler than diffing and companion tools change rarely — a plugin loading, not a
    // per-frame update.
    public void SetTools(IReadOnlyList<(string Title, Control View)> tools)
    {
        EmptyState.IsVisible = tools.Count == 0;
        ToolsHost.IsVisible = tools.Count > 0;
        ToolsHost.Items.Clear();
        foreach (var (title, view) in tools)
        {
            var label = new TextBlock { Text = title, FontSize = 11 };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
            ToolsHost.Items.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    label,
                    view,
                },
            });
        }
    }

    // AC-241: let the window manager move it, including compositors that do not allow absolute positioning.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HeaderBar).Properties.IsLeftButtonPressed || CloseButton.IsPointerOver)
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
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
