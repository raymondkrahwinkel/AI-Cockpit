using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private PixelPoint _dragGrabOffset;
    private bool _dragging;

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
        HeaderBar.PointerMoved += _OnHeaderMoved;
        HeaderBar.PointerReleased += _OnHeaderReleased;
        HeaderBar.PointerCaptureLost += (_, _) => _EndDrag();
        CloseButton.Click += (_, _) => Hide();

        // Only the position is worth restoring: this window is SizeToContent, so a saved width/height would
        // never apply the way AssistantChatWindow's HasUsableSize gate expects for its own resizable window.
        var saved = _windowBoundsStore?.LoadAsync(BoundsKey).GetAwaiter().GetResult();
        if (saved is not null && RestoredWindowBounds.IsOnAScreen(saved, Screens.All.Select(s => s.WorkingArea)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(saved.X, saved.Y);
        }
    }

    // Rebuilds the hosted tools from the registry (CompanionWindowPresenter calls this on every registry change).
    // Replacing the whole list is simpler than diffing and companion tools change rarely — a plugin loading, not a
    // per-frame update.
    public void SetTools(IReadOnlyList<(string Title, Control View)> tools)
    {
        ToolsHost.Items.Clear();
        foreach (var (title, view) in tools)
        {
            ToolsHost.Items.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 11, Foreground = _ToolTitleBrush },
                    view,
                },
            });
        }
    }

    // AC-334: looked up rather than a literal — these labels are built in code, not XAML, but the token still
    // has to come from Theme.axaml's single source of truth like every other cockpit colour.
    private static Avalonia.Media.IBrush _ToolTitleBrush =>
        (Avalonia.Media.IBrush)Avalonia.Application.Current!.FindResource("CockpitTextSecondaryBrush")!;

    // Absolute-position drag, the same technique AssistantChatWindow uses for its header — without that
    // window's drop-to-dock zone, which is a feature of the assistant, not of a borderless draggable window.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }

        _dragging = true;
        _dragGrabOffset = this.PointToScreen(e.GetPosition(this)) - Position;
        e.Pointer.Capture(HeaderBar);
    }

    private void _OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Position = this.PointToScreen(e.GetPosition(this)) - _dragGrabOffset;
    }

    private void _OnHeaderReleased(object? sender, PointerReleasedEventArgs e) => _EndDrag();

    private void _EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _ = _SavePositionAsync();
    }

    private async Task _SavePositionAsync()
    {
        if (_windowBoundsStore is null)
        {
            return;
        }

        try
        {
            await _windowBoundsStore.SaveAsync(BoundsKey, new WindowBounds(Position.X, Position.Y, (int)Width, (int)Height, IsMaximized: false))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort, same as AssistantChatWindow's own position save.
        }
    }
}
