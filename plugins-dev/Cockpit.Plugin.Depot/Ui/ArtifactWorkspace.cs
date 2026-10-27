using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Depot.UI;

// AC-1789: one pinned, read-only Depot artifact tab. The browser owns routing; this control owns safe loading and display.
internal sealed class ArtifactWorkspace : UserControl
{
    public const string WorkspaceTypeId = "depot.artifact";
    public const long MaxInlineBytes = 2 * 1024 * 1024;

    private readonly ICockpitUiHost? _host;
    private readonly DockPanel _body = new() { Margin = new Thickness(18) };
    private DepotBrowserModel? _model;
    private string? _project;
    private DepotBrowserNode? _node;

    public ArtifactWorkspace(ICockpitUiHost host, IWorkspaceContext context, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        _host = host;
        Content = _body;
        _ = _LoadAsync(context.InstanceKey, connections);
    }

    internal ArtifactWorkspace(ICockpitUiHost host, DepotBrowserNode node, Func<CancellationToken, Task<byte[]?>> read)
    {
        _host = host;
        Content = _body;
        _Show(node, read);
    }

    internal ArtifactWorkspace(DepotBrowserNode node, byte[] content)
    {
        Content = _body;
        _Show(node, _ => Task.FromResult<byte[]?>(content));
    }

    private async Task _LoadAsync(string? instanceKey, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        if (!_TryParseKey(instanceKey, out var connectionId, out var project, out var path))
        {
            _Empty("This tab no longer identifies a Depot file.");
            return;
        }

        var connection = connections.FirstOrDefault(candidate => candidate.Id == connectionId);
        if (connection is null)
        {
            _Empty("The Depot connection for this tab is no longer configured.");
            return;
        }

        _model = new DepotBrowserModel(_host!, connection, new HttpClient());
        _project = project;
        var tree = await _model.TreeAsync(project).ConfigureAwait(true);
        var node = tree.Data?.Nodes.FirstOrDefault(candidate => candidate.Path == path);
        if (tree.Status != DepotBrowserStatus.Ok || node is null)
        {
            _Empty(tree.Message ?? "The file no longer exists in Depot.");
            return;
        }

        _Show(node, cancellation => _ReadAsync(node, cancellation));
    }

    private async Task<byte[]?> _ReadAsync(DepotBrowserNode node, CancellationToken cancellationToken)
    {
        var answer = await _model!.ReadBytesAsync(_project!, node, cancellationToken).ConfigureAwait(true);
        if (answer.Status == DepotBrowserStatus.Ok)
        {
            return answer.Data;
        }

        _Empty(answer.Message ?? "Cockpit could not load this file.");
        return null;
    }

    private void _Show(DepotBrowserNode node, Func<CancellationToken, Task<byte[]?>> read)
    {
        _node = node;
        _body.Children.Clear();
        var header = _Header(node);
        DockPanel.SetDock(header, Dock.Top);
        _body.Children.Add(header);
        switch (_Kind(node))
        {
            case _ArtifactKind.TooLarge:
                _Empty($"This file is larger than the {MaxInlineBytes / 1024 / 1024} MiB Cockpit preview limit.", keepHeader: true);
                return;
            case _ArtifactKind.Unknown:
                _Empty("Cockpit cannot preview this file type.", keepHeader: true);
                return;
            case _ArtifactKind.Browser:
                _Empty("Open this HTML or PDF file in your browser from Depot files.", keepHeader: true);
                return;
        }

        _ = _ShowContentAsync(node, read);
    }

    private async Task _ShowContentAsync(DepotBrowserNode node, Func<CancellationToken, Task<byte[]?>> read)
    {
        var bytes = await read(CancellationToken.None).ConfigureAwait(true);
        if (bytes is null)
        {
            return;
        }

        if (_Kind(node) == _ArtifactKind.Image)
        {
            try
            {
                var bitmap = new Bitmap(new MemoryStream(bytes));
                var dimensions = new TextBlock { Text = $"{bitmap.PixelSize.Width} x {bitmap.PixelSize.Height} px", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 0) };
                DockPanel.SetDock(dimensions, Dock.Bottom);
                _body.Children.Add(dimensions);
                _body.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
            catch (ArgumentException)
            {
                _Empty("Cockpit could not decode this image.", keepHeader: true);
            }

            return;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var editor = _host?.CreateMarkdownEditor(text);
        if (editor is not null)
        {
            editor.IsReadOnly = true;
            editor.ShowSource = !Path.GetExtension(node.Path).Equals(".md", StringComparison.OrdinalIgnoreCase);
            _body.Children.Add(editor.View);
            return;
        }

        _body.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.NoWrap });
    }

    private Control _Header(DepotBrowserNode node)
    {
        var title = new TextBlock { Text = Path.GetFileName(node.Path), FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var download = new Button { Content = "Download" };
        download.Click += async (_, _) => await _DownloadAsync().ConfigureAwait(true);
        actions.Children.Add(download);
        DockPanel.SetDock(actions, Dock.Right);
        var header = new DockPanel();
        header.Children.Add(actions);
        header.Children.Add(title);
        return header;
    }

    private async Task _DownloadAsync()
    {
        if (_model is null || _project is null || _node is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var answer = await _model.DownloadAsync(_project, _node, async name =>
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name }).ConfigureAwait(true);
            return file is null ? null : await file.OpenWriteAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        if (answer.Status == DepotBrowserStatus.Ok && answer.Data)
        {
            _host?.ShowToast($"Saved {DepotDownload.SafeFileName(_node.Path)}.");
        }
    }

    private void _Empty(string reason, bool keepHeader = false)
    {
        if (!keepHeader)
        {
            _body.Children.Clear();
        }

        _body.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
    }

    internal static bool CanOpenInCockpit(DepotBrowserNode node) => _Kind(node) is _ArtifactKind.Image or _ArtifactKind.Text or _ArtifactKind.TooLarge or _ArtifactKind.Unknown;

    private static _ArtifactKind _Kind(DepotBrowserNode node)
    {
        if (node.Size > MaxInlineBytes)
        {
            return _ArtifactKind.TooLarge;
        }

        var extension = Path.GetExtension(node.Path);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return _ArtifactKind.Browser;
        }

        if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return _ArtifactKind.Image;
        }

        if (node.Kind == DepotBrowserKind.Document || node.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true || new[] { ".txt", ".json", ".yaml", ".yml", ".csv", ".log", ".md", ".xml", ".cs", ".js", ".ts", ".css", ".sql", ".sh", ".ps1" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return _ArtifactKind.Text;
        }

        return _ArtifactKind.Unknown;
    }

    private static bool _TryParseKey(string? value, out string connectionId, out string project, out string path)
    {
        connectionId = project = path = string.Empty;
        if (value is null || !value.StartsWith("depot:", StringComparison.Ordinal))
        {
            return false;
        }

        var pieces = value.Split(':', 4);
        if (pieces.Length != 4 || pieces.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        connectionId = pieces[1];
        project = pieces[2];
        path = pieces[3];
        return true;
    }

    private enum _ArtifactKind { Image, Text, Browser, TooLarge, Unknown }
}
