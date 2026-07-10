using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Shows a plugin's content in a modal window over the cockpit's main window (#14). The window carries the
/// cockpit background so a plugin dialog looks native; the plugin owns the content control inside it.
/// </summary>
internal sealed class PluginDialogHost : IPluginDialogHost, ISingletonService
{
    public async Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = createContent(),
        };

        if (Application.Current.TryFindResource("CockpitPanelBgBrush", out var background) && background is IBrush brush)
        {
            window.Background = brush;
        }

        await window.ShowDialog(owner);
    }
}
