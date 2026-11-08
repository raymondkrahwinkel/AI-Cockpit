using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Views.Editor;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

// A live file in a plain control: the workspace's live document, presence feed and editor without its tab, save and compare chrome.
internal sealed class LiveMarkdownEditorView : UserControl, ILiveMarkdownEditor
{
    private readonly FileSourceChannelClient _client;
    private readonly FileRef _file;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MarkdownEditor _editor = new() { IsReadOnly = true };
    private readonly LivePresenceBar _people = new();
    private readonly TextBlock _typing = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Text = "Connecting…" };
    private LiveTextDocument? _live;
    private LivePresenceFeed? _presence;
    private IReadOnlyList<LivePeer> _peers = [];
    private bool _disposed;

    public LiveMarkdownEditorView(FileSourceChannelClient client, FileRef file, ILogger logger)
    {
        (_client, _file, _logger) = (client, file, logger);
        _editor.HideToolbar();
        _typing.Bind(TextBlock.ForegroundProperty, _typing.GetResourceObservable("CockpitTextSecondaryBrush"));
        _state.Bind(TextBlock.ForegroundProperty, _state.GetResourceObservable("CockpitTextFaintBrush"));
        var top = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_people, Dock.Right);
        top.Children.Add(_people);
        top.Children.Add(_typing);
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_state, Dock.Bottom);
        _state.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        root.Children.Add(top);
        root.Children.Add(_state);
        root.Children.Add(new Border { MinHeight = 200, Child = _editor });
        Content = root;
        _ = _StartAsync();
    }

    public Control View => this;

    public IReadOnlyList<LivePeer> Peers => _peers;

    public event EventHandler? PeersChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _presence?.Dispose();
        // Closing the session cannot be awaited here; the document logs a failure and the source times the session out.
        _live?.Dispose();
    }

    private async Task _StartAsync()
    {
        var live = new LiveTextDocument(_editor.TextEditor.Document, _client, _logger);
        try
        {
            var (id, snapshot, wire) = await _client.OpenLiveAsync(_file, _lifetime.Token).WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                live.Dispose();
                await _client.CloseLiveAsync(id, CancellationToken.None).ConfigureAwait(true);
                return;
            }

            _editor.Text = snapshot.Text;
            live.Attach(id, snapshot);
            live.Changed += (_, _) => _Show(live.Status);
            live.ExternalEdited += (_, edit) => _editor.MarkExternalEdit(edit);
            _live = live;
            _Show(snapshot.Status);
            if (wire.Peers is { } peers)
            {
                _presence = new LivePresenceFeed(_client, _editor, _people, _logger);
                _presence.Shown += _OnPeers;
                await _presence.AttachAsync(id, peers).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or FileSourceException)
        {
            live.Dispose();
            if (!_disposed)
            {
                _logger.LogWarning(error, "Opening a live file in a plain editor failed.");
                _state.Text = "This note could not be opened live.";
            }
        }
    }

    private void _Show(LiveFileStatus status)
    {
        _editor.IsReadOnly = status.State == LiveFileState.ReadOnly;
        _state.Text = status.State switch
        {
            LiveFileState.Live => "",
            LiveFileState.Connecting => "Connecting…",
            _ => status.Reason ?? status.State.ToString(),
        };
        _state.IsVisible = _state.Text.Length > 0;
    }

    private void _OnPeers(IReadOnlyList<LivePeer> peers)
    {
        _peers = peers;
        var typing = peers.Where(peer => !peer.You && !peer.Away && peer.Activity == "editing").Select(peer => peer.Name).ToList();
        _typing.Text = typing.Count switch
        {
            0 => "",
            1 => $"{typing[0]} is typing",
            2 => $"{typing[0]} and {typing[1]} are typing",
            _ => $"{typing.Count} people are typing",
        };
        Dispatcher.UIThread.Post(() => PeersChanged?.Invoke(this, EventArgs.Empty));
    }
}
