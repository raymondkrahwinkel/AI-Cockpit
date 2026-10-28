using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Layout;

namespace Cockpit.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    // The main window's screen is the splash's, so the two never open on different monitors.
    public SplashWindow(SplashViewModel viewModel, WindowBounds? main)
        : this()
    {
        DataContext = viewModel;
        var screen = main is { HasUsableSize: true }
            ? Screens.All.FirstOrDefault(candidate => RestoredWindowBounds.IsOnAScreen(main, [candidate.WorkingArea]))
            : null;
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(
                area.X + (int)((area.Width - Width * screen.Scaling) / 2),
                area.Y + (int)((area.Height - Height * screen.Scaling) / 2));
        }
    }

    public async Task FadeOutAsync()
    {
        Card.Opacity = 0;
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Close();
    }
}
