using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Logging;
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

    public void TakeFocus()
    {
        Activate();
        PasswordBox.Focus();
    }

    private UnlockViewModel? Unlock => (DataContext as SplashViewModel)?.Unlock;

    private async void OnUnlock(object? sender, RoutedEventArgs e) => await _UnlockAsync();

    private async void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _UnlockAsync();
        }
    }

    private async Task _UnlockAsync()
    {
        try
        {
            if (Unlock is { } unlock)
            {
                await unlock.UnlockAsync();
                PasswordBox.Focus();
            }
        }
        catch (Exception exception)
        {
            _ReportUnlockError(exception);
        }
    }

    private async void OnForgot(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Unlock is { IsBusy: false } unlock && await UnlockWindow.ConfirmResetAsync(this))
            {
                await unlock.ResetAsync();
            }

            PasswordBox.Focus();
        }
        catch (Exception exception)
        {
            _ReportUnlockError(exception);
        }
    }

    private void _ReportUnlockError(Exception exception)
    {
        LifecycleLog.Write($"Unlock failed: {exception}");
        if (Unlock is { } unlock)
        {
            unlock.Error = "Could not unlock the credentials. Try again.";
        }
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
