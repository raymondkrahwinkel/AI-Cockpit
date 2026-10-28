using System.Text.Json;
using Avalonia.Threading;
using Cockpit.App.Views.Editor;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

// Connects a live session's presence to the editor and the header: what the source reports is drawn, and this editor's own
// caret and selection go back to the source a moment after they settle. The source owns the rhythm and the colours.
internal sealed class LivePresenceFeed : IDisposable
{
    private readonly FileSourceChannelClient _client;
    private readonly MarkdownEditor _editor;
    private readonly LivePresenceBar _bar;
    private readonly ILogger _logger;
    private readonly IDisposable _events;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource _life = new();
    private string? _id;

    public LivePresenceFeed(FileSourceChannelClient client, MarkdownEditor editor, LivePresenceBar bar, ILogger logger)
    {
        _client = client;
        _editor = editor;
        _bar = bar;
        _logger = logger;
        _events = client.Subscribe(FileSourceChannel.LivePresence, evt => Dispatcher.UIThread.Post(() => _OnPresence(evt.Payload)));
        _settle.Tick += async (_, _) =>
        {
            _settle.Stop();
            await _SendAsync().ConfigureAwait(true);
        };
        var area = editor.TextEditor.TextArea;
        area.Caret.PositionChanged += _OnMoved;
        area.SelectionChanged += _OnMoved;
        editor.PropertyChanged += _OnEditorProperty;
    }

    // The snapshot's peers: the source shows presence for this file.
    public Task AttachAsync(string sessionId, IReadOnlyList<LivePeer> peers)
    {
        _id = sessionId;
        _Show(peers);
        return _SendAsync();
    }

    public void Dispose()
    {
        _settle.Stop();
        _events.Dispose();
        var area = _editor.TextEditor.TextArea;
        area.Caret.PositionChanged -= _OnMoved;
        area.SelectionChanged -= _OnMoved;
        _editor.PropertyChanged -= _OnEditorProperty;
        _life.Cancel();
        _life.Dispose();
    }

    private void _Show(IReadOnlyList<LivePeer> peers)
    {
        _bar.SetPeers(peers);
        _editor.SetPeers(peers);
    }

    private void _OnPresence(JsonElement payload)
    {
        try
        {
            var presence = payload.Deserialize<FileSourceLivePresence>(FileSourceChannel.Json);
            if (presence is { Peers: not null } && presence.SessionId == _id)
            {
                _Show(presence.Peers);
            }
        }
        catch (JsonException error)
        {
            _logger.LogWarning(error, "A presence update from the file source could not be read.");
        }
    }

    private void _OnEditorProperty(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MarkdownEditor.IsReadOnlyProperty)
        {
            _OnMoved(this, EventArgs.Empty);
        }
    }

    private void _OnMoved(object? sender, EventArgs e)
    {
        if (_id is not null)
        {
            _settle.Stop();
            _settle.Start();
        }
    }

    private async Task _SendAsync()
    {
        if (_id is not { } id || _life.IsCancellationRequested)
        {
            return;
        }

        var area = _editor.TextEditor.TextArea;
        var selection = area.Selection.IsEmpty ? null : new LiveRange(area.Selection.SurroundingSegment.Offset, area.Selection.SurroundingSegment.EndOffset);
        var activity = _editor.IsReadOnly ? "watching" : selection is null ? "editing" : "selecting";
        try
        {
            await _client.SetPresenceAsync(id, new LivePresenceState(activity, area.Caret.Offset, selection), _life.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (FileSourceException error)
        {
            _logger.LogDebug(error, "The file source did not take this editor's cursor.");
        }
    }
}
