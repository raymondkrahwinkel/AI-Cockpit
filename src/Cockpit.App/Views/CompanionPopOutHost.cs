using Avalonia;
using Avalonia.Controls;
using Cockpit.Core.Layout;

namespace Cockpit.App.Views;

// Opens pop-outs for a strip and keeps them where CompanionPopOutPlacer puts them.
internal sealed class CompanionPopOutHost
{
    private readonly Window _strip;
    private readonly List<CompanionPopOut> _open = [];

    public CompanionPopOutHost(Window strip)
    {
        _strip = strip;
        strip.PositionChanged += (_, _) => Relayout();
        if (strip.Screens is { } screens)
        {
            screens.Changed += (_, _) => Relayout();
        }
    }

    public CompanionOrientation Orientation { get; set; } = CompanionOrientation.Horizontal;

    // Wayland cannot place a window absolutely; the compositor anchors it to the strip instead (not yet tried live).
    private static bool _IsWayland => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";

    public bool IsOpen(string key) => _open.Exists(p => p.PopOutKey == key);

    public void CloseAll(params string[] exceptKeys)
    {
        foreach (var popOut in _open.Where(popOut => !exceptKeys.Contains(popOut.PopOutKey)).ToArray())
        {
            popOut.Close();
        }
    }

    public void Close(string key) => _open.Find(popOut => popOut.PopOutKey == key)?.Close();

    // A second click on the same item closes it. False means there is no room; the caller then holds its message.
    public bool Toggle(string key, Control item, Control content, double width, bool showActivated = true)
    {
        if (_open.Find(p => p.PopOutKey == key) is { } existing)
        {
            existing.Close();

            return true;
        }

        var popOut = new CompanionPopOut { PopOutKey = key, Body = content, ShowActivated = showActivated, Topmost = _strip.Topmost };
        if (_IsWayland)
        {
            popOut.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else if (!_Place(popOut, item, width, [.. _open.Select(p => p.CardBounds)]))
        {
            return false;
        }

        popOut.Closed += (_, _) => _open.Remove(popOut);
        _open.Add(popOut);
        popOut.Show(_strip);

        return true;
    }

    private void Relayout()
    {
        if (_IsWayland)
        {
            return;
        }

        var placed = new List<PixelRect>();
        foreach (var popOut in _open.ToArray())
        {
            if (popOut.Tag is Control item && _Place(popOut, item, popOut.Width - 32, placed))
            {
                placed.Add(popOut.CardBounds);
            }
            else
            {
                popOut.Close();
            }
        }
    }

    private bool _Place(CompanionPopOut popOut, Control item, double width, IReadOnlyList<PixelRect> others)
    {
        var screen = _strip.Screens.ScreenFromWindow(_strip) ?? _strip.Screens.Primary;
        if (screen is null)
        {
            return false;
        }

        var scale = _strip.RenderScaling;
        var origin = item.PointToScreen(default);
        var itemRect = new PixelRect(origin.X, origin.Y, (int)(item.Bounds.Width * scale), (int)(item.Bounds.Height * scale));
        var stripRect = new PixelRect(_strip.Position, new PixelSize((int)(_strip.Bounds.Width * scale), (int)(_strip.Bounds.Height * scale)));
        if (CompanionPopOutPlacer.Place(screen.WorkingArea, stripRect, Orientation, itemRect, popOut.DesiredCardSize(width), others) is not { } placement)
        {
            return false;
        }

        popOut.Tag = item;
        popOut.PlaceAt(placement.Bounds);

        return true;
    }
}
