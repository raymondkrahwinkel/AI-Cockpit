using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.UI;

internal sealed partial class DepotBrowserPanel
{
    private IDisposable? _fileSavedSubscription;

    private Task _EditMarkdownAsync(DepotBrowserNode node) =>
        _model is { } model && _project is { } project && _loaded.CanShare
            ? DepotMarkdownFiles.OpenAsync(_host, model.Connection, project, node.Path, edit: true)
            : Task.CompletedTask;

    private Task _NewMarkdownAsync(string folder)
    {
        if (_model is not { } model || _project is not { } project || !_loaded.CanShare) return Task.CompletedTask;
        var folders = new[] { "" }.Concat(_loaded.Nodes.SelectMany(node => _ParentFolders(node.Path)))
            .Append(folder).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return _host.ShowDialogAsync("New Markdown file", () => new NewMarkdownFileDialog(
            (_projectBox.SelectedItem as DepotBrowserProject)?.Name ?? project, folder, folders,
            path => _loaded.Nodes.Any(node => node.Path == path),
            path => DepotMarkdownFiles.OpenAsync(_host, model.Connection, project, path, edit: true, createNew: true)),
            480, 480);
    }

    private static IEnumerable<string> _ParentFolders(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) yield return path[..slash];
    }

    private void _AddMarkdownMenu(TreeViewItem row, DepotTreeItem item)
    {
        if (!_loaded.CanShare) return;
        var menu = new MenuItem { Header = item.Kind == DepotTreeKind.Folder ? "New Markdown file here…" : "Edit" };
        if (item.Kind == DepotTreeKind.Folder)
            menu.Click += async (_, _) => await _NewMarkdownAsync(item.Path).ConfigureAwait(true);
        else if (item.Node is { } node && DepotMarkdownFiles.IsMarkdown(node))
            menu.Click += async (_, _) => await _EditMarkdownAsync(node).ConfigureAwait(true);
        else return;
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { menu } };
    }

    private void _ListenForFileSaves()
    {
        void Subscribe() => _fileSavedSubscription ??= _host.Channel.Subscribe(FileSourceChannel.Saved, evt =>
        {
            try
            {
                if (evt.Payload.Deserialize<FileRef>(FileSourceChannel.Json) is { } file)
                    Dispatcher.UIThread.Post(() => _ = _RefreshAfterFileSaveAsync(file));
            }
            catch (JsonException) { }
        });
        Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { _fileSavedSubscription?.Dispose(); _fileSavedSubscription = null; };
    }

    private async Task _RefreshAfterFileSaveAsync(FileRef file)
    {
        if (_fileSavedSubscription is null || _model is not { } model || _project is not { } project
            || file.ConnectionId != model.Connection.Id) return;
        try
        {
            var source = DepotFileRefs.Parse(file, model.Connection.Id);
            if (source.Project == project) await LoadTreeAsync(source.Path).ConfigureAwait(true);
        }
        catch (FileSourceException) { }
        catch (OperationCanceledException) { }
        catch (Exception) { _host.ShowToast("Could not refresh Depot files after saving."); }
    }
}
