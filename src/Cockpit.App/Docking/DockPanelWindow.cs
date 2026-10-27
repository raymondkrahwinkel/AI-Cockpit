using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions.Docking;

namespace Cockpit.App.Docking;

// AC-1710: the window a floating dock panel stands in (mockup AC-1708 screen 3) — the cockpit's chrome with "Dock
// back" in the bar, around the panel's own view. `DockPanelHost` owns when it opens and closes; this only keeps
// its bounds, under the panel's id in the keyed window-bounds store (AC-866), the key the assistant already uses.
internal sealed class DockPanelWindow : Window
{
    private readonly IWindowBoundsStore? _boundsStore;
    private readonly string _boundsKey;

    // Holds the panel's view inside the chrome, so the view alone can be let go of when the panel moves on.
    private readonly ContentControl _body;

    // The last normal (non-maximized) bounds — what a maximized window saves as "restore to", as MainWindow does.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public event EventHandler? DockBackRequested;

    internal DockPanelWindow(DockPanelRegistration panel, Control view, IWindowBoundsStore? boundsStore)
        : this(panel.Id, panel.Title, view, boundsStore, "Put this panel back in the dock rail")
    {
    }

    // AC-1751: also the window a floating workspace stands in, its bounds under the workspace id.
    internal DockPanelWindow(string boundsKey, string title, Control view, IWindowBoundsStore? boundsStore, string dockBackTip)
    {
        _boundsStore = boundsStore;
        _boundsKey = boundsKey;
        Title = $"Cockpit — {title}";
        Width = 960;
        Height = 640;
        MinWidth = 340;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _body = new ContentControl { Content = view };
        Content = _body;

        var dockBack = new Button { Content = "⇤ Dock back", Padding = new Thickness(10, 3), FontSize = 12 };
        dockBack.Classes.Add("Subtle");
        ToolTip.SetTip(dockBack, dockBackTip);
        dockBack.Click += (_, _) => DockBackRequested?.Invoke(this, EventArgs.Empty);

        CockpitWindowChrome.Apply(this, Title, includeMinimize: true, includeMaximize: true, closeOnEscape: false, captionAction: dockBack);
        _RestoreBounds();

        // A move without a resize; read here rather than in OnClosed, where the closed window reports (0,0).
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalPosition = e.Point;
            }
        };
    }

    // Before Show(), like the assistant's window: Manual placement, since there is no owner to centre on.
    private void _RestoreBounds()
    {
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);

        var saved = _boundsStore?.LoadAsync(_boundsKey).GetAwaiter().GetResult();
        if (saved is not { HasUsableSize: true } || !RestoredWindowBounds.IsOnAScreen(saved, Screens.All.Select(s => s.WorkingArea)))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(saved.X, saved.Y);
        Width = saved.Width;
        Height = saved.Height;
        _normalPosition = Position;
        _normalSize = new Size(saved.Width, saved.Height);

        if (saved.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    // The panel's view leaves with the panel: a `KeepAlive` view is the same instance the rail takes next.
    internal void ReleaseView() => _body.Content = null;

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = new Size(Width, Height);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _ = _SaveBoundsAsync();
        base.OnClosed(e);
    }

    // Best-effort and fire-and-forget: a window closing never ends the app, so nothing waits on this write.
    private async Task _SaveBoundsAsync()
    {
        if (_boundsStore is null)
        {
            return;
        }

        try
        {
            await _boundsStore.SaveAsync(_boundsKey, new WindowBounds(
                _normalPosition.X,
                _normalPosition.Y,
                (int)_normalSize.Width,
                (int)_normalSize.Height,
                WindowState == WindowState.Maximized)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
