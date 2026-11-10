using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.LocalFiles.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.LocalFiles.UI;

// The native picker only sees this computer's disk, so a server's folders are listed by the plugin running there.
internal sealed class ServerFolderPicker : UserControl
{
    private readonly IPluginUiChannel _channel;
    private readonly Action<string> _select;
    private readonly TextBlock _path = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Classes = { "optionsHint" } };
    private readonly ListBox _list = new();
    private readonly Button _up = new() { Content = "Up", Classes = { "Compact" } };
    private string? _current;

    public ServerFolderPicker(IPluginUiChannel channel, Action<string> select)
    {
        _channel = channel;
        _select = select;
        _path.Bind(TextBlock.FontFamilyProperty, _path.GetResourceObservable("CockpitMonoFont"));
        _list.DoubleTapped += async (_, _) => await _OpenAsync(_list.SelectedItem as string ?? _current).ConfigureAwait(true);
        _up.Click += async (_, _) => await _OpenAsync(Path.GetDirectoryName(_current ?? "")).ConfigureAwait(true);
        var cancel = new Button { Content = "Cancel", Classes = { "Ghost" } };
        cancel.Click += (_, _) => _Close();
        var choose = new Button { Content = "Select folder", Classes = { "Accent" } };
        choose.Click += (_, _) =>
        {
            if ((_list.SelectedItem as string ?? _current) is { } chosen)
            {
                _select(chosen);
                _Close();
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, choose } };
        var top = new DockPanel { Children = { _up, _path } };
        DockPanel.SetDock(_up, Dock.Left);
        _path.Margin = new Thickness(8, 0, 0, 0);
        _path.VerticalAlignment = VerticalAlignment.Center;
        var layout = new DockPanel { Margin = new Thickness(16) };
        foreach (var (part, dock) in new (Control, Dock)[] { (_hint, Dock.Top), (top, Dock.Top), (buttons, Dock.Bottom) })
        {
            DockPanel.SetDock(part, dock);
            part.Margin = new Thickness(0, 0, 0, 8);
            layout.Children.Add(part);
        }
        layout.Children.Add(_list);
        Content = layout;
        Ready = _OpenAsync(null);
    }

    internal Task Ready { get; }

    // A failed call stays in the dialog as a sentence; letting it escape a click would end the app.
    private async Task _OpenAsync(string? path)
    {
        try
        {
            var answer = (await _channel.InvokeAsync(LocalFilesChannel.Folders,
                JsonSerializer.SerializeToElement(new LocalPathRequest(path), LocalFilesChannel.Json)).ConfigureAwait(true))
                .Deserialize<LocalFolders>(LocalFilesChannel.Json);
            _current = answer?.Path;
            _list.ItemsSource = answer?.Children ?? [];
            _hint.Text = answer?.Error ?? "Folders on the server's disk. Double-click a folder to open it.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _hint.Text = "The server could not be asked for its folders. Check the connection and try again.";
        }
        _path.Text = _current ?? "Drives";
        _up.IsEnabled = _current is not null;
    }

    private void _Close() => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
