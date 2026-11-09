using Avalonia;
using Avalonia.Controls;
using Cockpit.Core.Configuration;

namespace Cockpit.App.Views;

// Floating "Listening"/"Transcribing" pill during push-to-talk — see VoicePushToTalkCoordinator.
// AC-636 adds `ShowActivated="False"` in markup: click-through alone only answers the pointer,
// showing a window still activates it (Win32) and steals the keyboard. Ported from the KDE/KWin spike.
public partial class VoiceOverlayWindow : Window
{
    private const int BottomGap = 48;
    private bool _clickThroughApplied;

    public VoiceOverlayWindow()
    {
        InitializeComponent();
        // Set here, not in markup: it's the product name plus a word, and a rename shouldn't have
        // to remember this window even though nobody reads it (no decorations, not in the taskbar).
        Title = $"{CockpitProduct.DisplayName} voice overlay";
        Opened += _OnOpened;
        // Fires once the real size settles: on first show, Bounds is still 0 before SizeToContent
        // measures it, and this also re-centres as the pill grows/shrinks between its two states.
        SizeChanged += (_, _) => PositionBottomCenter();
    }

    // Re-centres the pill bottom-centre — called before every show to cover a screen/resolution change between holds.
    public void PositionBottomCenter()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(Bounds.Width * screen.Scaling);
        var height = (int)(Bounds.Height * screen.Scaling);
        var x = area.X + ((area.Width - width) / 2);
        var y = area.Y + area.Height - height - BottomGap;
        Position = new PixelPoint(x, y);
    }

    private void _OnOpened(object? sender, EventArgs e)
    {
        PositionBottomCenter();
        _TryEnableClickThrough();
    }

    // X11 only, as before (Linux/XWayland, from the KDE/KWin spike); applied once, the shape persists for the window's lifetime.
    private void _TryEnableClickThrough()
    {
        if (_clickThroughApplied || !OperatingSystem.IsLinux())
        {
            return;
        }

        OverlayWindows.MakeClickThrough(this);
        _clickThroughApplied = true;
    }
}
