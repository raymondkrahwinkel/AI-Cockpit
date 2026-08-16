using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Whiteboard.Model;
using Cockpit.Plugin.Whiteboard.Rendering;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Whiteboard;

// AC-829: the panel as IWhiteboardAccessRegistry's producer — DiagramWorkspaceBody's pattern (AC-824/AC-810),
// narrowed to AC-823's one capability: sign up on open, show the coupling bar, keep the registry's snapshot in
// step with what the operator draws.
internal sealed class WhiteboardWorkspaceBody : UserControl
{
    private static readonly PixelSize SnapshotSize = new(800, 600);

    private readonly IWhiteboardAccessRegistry? _registry;
    private readonly IWhiteboardSnapshotRenderer _renderer = new WhiteboardSnapshotRenderer();
    private readonly WhiteboardControl _control;
    private readonly string _surfaceId;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private WhiteboardCoupling? _current;

    public WhiteboardWorkspaceBody(IWorkspaceContext context, ICockpitHost host)
    {
        _registry = host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry;
        _surfaceId = context.WorkspaceId;
        _control = new WhiteboardControl(new WhiteboardDocument());
        _control.Canvas.Changed += (_, _) => _registry?.UpdateSnapshot(_surfaceId, _Snapshot());

        (_couplingBar, _couplingLabel, _readChip) = _BuildCouplingBar();

        Content = new DockPanel { Children = { _couplingBar, _control } };
        DockPanel.SetDock(_couplingBar, Dock.Top);

        if (_registry is not null)
        {
            _registry.SurfaceOpened(_surfaceId, "Whiteboard", _Snapshot());
            _registry.CouplingChanged += _OnCouplingChanged;
            _RefreshCouplingBar();
        }

        DetachedFromVisualTree += (_, _) =>
        {
            if (_registry is null)
            {
                return;
            }

            _registry.CouplingChanged -= _OnCouplingChanged;
            _registry.SurfaceClosed(_surfaceId);
        };
    }

    private byte[] _Snapshot()
    {
        using var bitmap = _renderer.Render(_control.Canvas.Document, SnapshotSize);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private void _OnCouplingChanged(WhiteboardCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        _current = change.Coupling;
        Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshCouplingBar);
    }

    // The "agent connected" bar (AC-810/AC-824's precedent), narrowed to AC-823's one capability — no edit chip,
    // AC-820's fixed boundary for this surface.
    private (Border Bar, TextBlock Label, TextBlock ReadChip) _BuildCouplingBar()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = _Brush("CockpitAccentBrush") };
        var readChip = _Chip();
        var disconnect = new Button { Content = "Disconnect", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        disconnect.Click += (_, _) => _registry?.Disconnect(_surfaceId);

        var bar = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 4),
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitAccentBrush"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    disconnect,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { label, readChip },
                    },
                },
            },
        };
        DockPanel.SetDock(disconnect, Dock.Right);

        return (bar, label, readChip);
    }

    private void _RefreshCouplingBar()
    {
        _couplingBar.IsVisible = _current is not null;
        if (_current is not { } coupling)
        {
            return;
        }

        _couplingLabel.Text = coupling.CanRead
            ? $"Agent connected — session {coupling.SessionId}"
            : $"Agent connected — session {coupling.SessionId} (no capabilities granted yet)";
        _SetChip(_readChip, "read_whiteboard", coupling.CanRead);
    }

    private static TextBlock _Chip() => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(6, 1),
        FontSize = 10,
    };

    private static void _SetChip(TextBlock chip, string name, bool granted)
    {
        chip.Text = granted ? $"{name} allowed" : $"{name} not granted";
        chip.Foreground = granted ? _Brush("CockpitAccentBrush") : _Brush("CockpitTextSecondaryBrush");
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
