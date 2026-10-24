using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.App.ViewModels;
using System.Collections.Specialized;

namespace Cockpit.App.Views;

// AC-1474: remote plugins are administered here; credentials stay on the server and never enter this page.
public sealed class ServerPluginsPage : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private ServerAdminViewModel? _admin;

    public ServerPluginsPage()
    {
        var heading = new TextBlock { FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold };
        heading.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("PluginsCount") { StringFormat = "Plugins on the server · {0}" });
        var install = new Button { Content = "Install from store", HorizontalAlignment = HorizontalAlignment.Left };
        install.Bind(Button.CommandProperty, new Avalonia.Data.Binding("InstallPluginCommand"));
        var update = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        update.Bind(ContentControl.ContentProperty, new Avalonia.Data.Binding("UpdateLabel"));
        update.Bind(Button.CommandProperty, new Avalonia.Data.Binding("UpdatePluginsCommand"));
        update.Bind(IsVisibleProperty, new Avalonia.Data.Binding("HasAvailableUpdates"));
        var restart = new TextBlock { Text = "Plugin changes take effect after the server restarts.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var restartNow = new Button { Content = "Restart the server now", HorizontalAlignment = HorizontalAlignment.Left };
        restartNow.Bind(Button.CommandProperty, new Avalonia.Data.Binding("RestartServerCommand"));
        restartNow.Bind(IsVisibleProperty, new Avalonia.Data.Binding("NeedsRestart"));
        ToolTip.SetTip(restartNow, "Restarts the server so the plugin changes take effect. Its sessions stop; this cockpit stays open.");
        var statusText = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        statusText.Classes.Add("optionsHint");
        statusText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Status"));
        statusText.Bind(IsVisibleProperty, new Avalonia.Data.Binding("StatusIsError") { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        var statusError = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        statusError.Classes.Add("optionsHint");
        statusError.Classes.Add("error");
        statusError.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Status"));
        statusError.Bind(IsVisibleProperty, new Avalonia.Data.Binding("StatusIsError"));
        var status = new Panel { Children = { statusText, statusError } };
        status.Bind(IsVisibleProperty, new Avalonia.Data.Binding("Status.Length"));
        var desktopRestart = new TextBlock { Text = "A plugin installed on this desktop loads when the desktop restarts.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var desktopRestartNow = new Button { Content = "Restart the cockpit now", HorizontalAlignment = HorizontalAlignment.Left };
        desktopRestartNow.Bind(Button.CommandProperty, new Avalonia.Data.Binding("RestartDesktopNowCommand"));
        var desktopPrompt = new StackPanel { Spacing = 8, Children = { desktopRestart, desktopRestartNow } };
        desktopPrompt.Bind(IsVisibleProperty, new Avalonia.Data.Binding("DesktopNeedsRestart"));
        Content = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12, Children = { heading, install, update, status, desktopPrompt, _rows, restart, restartNow } };
        DataContextChanged += (_, _) => _SetAdmin();
    }

    private void _SetAdmin()
    {
        if (_admin is not null)
        {
            _admin.Plugins.CollectionChanged -= _PluginsChanged;
        }

        _admin = DataContext as ServerAdminViewModel;
        if (_admin is not null)
        {
            _admin.Plugins.CollectionChanged += _PluginsChanged;
        }

        _ShowRows();
    }

    private void _PluginsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ShowRows();

    private void _ShowRows()
    {
        _rows.Children.Clear();
        if (DataContext is not ServerAdminViewModel admin)
        {
            return;
        }

        foreach (var row in admin.Plugins)
        {
            var state = new TextBlock();
            state.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ServerPluginRowViewModel.State)) { Source = row });
            var enable = new Button { Content = "Enable / disable", Command = admin.TogglePluginCommand, CommandParameter = row };
            var remove = new Button { Content = "Remove", Command = admin.RemovePluginCommand, CommandParameter = row };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { enable } };
            if (row.HasSettings)
            {
                actions.Children.Insert(0, new Button { Content = "Settings", Command = admin.OpenPluginSettingsCommand, CommandParameter = row });
            }

            var available = new TextBlock();
            available.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ServerPluginRowViewModel.UpdateText)) { Source = row });
            available.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ServerPluginRowViewModel.HasUpdate)) { Source = row });
            var onDesktop = new Button { Content = "Install on this desktop", Command = admin.InstallPluginOnDesktopCommand, CommandParameter = row };
            onDesktop.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ServerPluginRowViewModel.MissingOnDesktop)) { Source = row });
            ToolTip.SetTip(onDesktop, "This desktop does not have the plugin. Installs it from this desktop's own plugin stores, with the usual checks and consent; nothing is loaded from the server.");
            actions.Children.Insert(0, onDesktop);
            Grid.SetColumn(actions, 1);
            var removal = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { remove } };
            Grid.SetColumn(removal, 2);
            _rows.Children.Add(new Border
            {
                Padding = new Thickness(12),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    Children =
                    {
                        new StackPanel { Children = { new TextBlock { Text = $"{row.Name} {row.Version}", FontWeight = Avalonia.Media.FontWeight.SemiBold }, state, available } },
                        actions,
                        removal,
                    },
                },
            });
        }
    }
}
