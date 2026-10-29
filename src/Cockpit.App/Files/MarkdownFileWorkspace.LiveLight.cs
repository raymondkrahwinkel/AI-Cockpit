using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Core.Markdown;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

internal sealed partial class MarkdownFileWorkspace
{
    private readonly DispatcherTimer _liveLightTimer = new();
    private readonly TextBlock _localLines = new() { Name = "LocalLineCount", FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
    private int _retrySeconds = 3;
    private bool _liveLightPending;
    private bool _wasLiveLight;
    private bool _needsFileRefresh;
    private string? _lastLiveText;
    private string? _fallbackOrigin;
    private string? _conflictMine;
    private string? _conflictMerged;
    private string? _pendingLiveId;

    private bool _HasPendingFileText => _liveLightPending && Editor.Text != _savedText;

    private bool _IsLiveLight => _live?.Status.RequiresRejoin == true
        || _live?.Status.State is LiveFileState.Offline or LiveFileState.NoSaver or LiveFileState.Stale;

    private void _UpdateLiveLight()
    {
        if (_live?.Status.State == LiveFileState.Live)
        {
            _lastLiveText = _live.SourceText;
        }
        if (_loaded && _IsLiveLight && !_wasLiveLight)
        {
            _needsFileRefresh = true;
            _fallbackOrigin = _lastLiveText;
        }
        _wasLiveLight = _loaded && _IsLiveLight;
        _liveLightPending = Editor.Text != _savedText && (_liveLightPending || _IsLiveLight && _loaded);
        var local = new List<int>();
        if (_liveLightPending && _loaded)
        {
            var number = 0;
            foreach (var line in LineMerge.Compare(_savedText.Split('\n'), Editor.Text.Split('\n')))
            {
                if (line.Change != LineChange.OnlyTheirs)
                {
                    number++;
                    if (line.Change == LineChange.OnlyMine)
                    {
                        local.Add(number);
                    }
                }
            }
        }
        Editor.SetLocalLines(local);
        _localLines.Text = $"{local.Count} lines only in this Cockpit";
        _localLines.IsVisible = local.Count > 0;
        if (_IsLiveLight && (IsDirty || _live?.Status.RequiresRejoin == true) && !_saving && !_conflict && _canWrite && !_closed)
        {
            if (!_liveLightTimer.IsEnabled)
            {
                _liveLightTimer.Interval = TimeSpan.FromSeconds(_retrySeconds);
                _liveLightTimer.Start();
            }
        }
        else
        {
            _liveLightTimer.Stop();
        }
    }

    private async Task _SaveLiveLightAsync()
    {
        if (_conflict)
        {
            return;
        }
        _saving = true;
        _error = null;
        _UpdateState();
        try
        {
            if (_needsFileRefresh)
            {
                var current = await _ReadSnapshotAsync().ConfigureAwait(true);
                if (_fallbackOrigin is not null && !_MergeLiveLight(current, _fallbackOrigin))
                {
                    return;
                }
                _needsFileRefresh = false;
                if (Editor.Text == _savedText)
                {
                    return;
                }
            }
            for (var attempt = 0; attempt < 3 && _IsLiveLight && !_closed; attempt++)
            {
                var submitted = Editor.Text;
                var bytes = Encoding.UTF8.GetBytes(submitted);
                using var content = new MemoryStream(_hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes, writable: false);
                var result = await _client.WriteAsync(_options.File, content, _version, false, _lifetime.Token).ConfigureAwait(true);
                if (result.Status == FileWriteStatus.Written)
                {
                    _savedText = submitted;
                    _version = result.Version;
                    _retrySeconds = 3;
                    return;
                }
                var theirs = await _ReadSnapshotAsync().ConfigureAwait(true);
                if (!_MergeLiveLight(theirs))
                {
                    return;
                }
                if (Editor.Text == _savedText)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (FileSourceException error)
        {
            _error = error.Message;
            _retrySeconds = Math.Min(60, _retrySeconds * 2);
        }
        finally
        {
            await _RecoverLiveAsync().ConfigureAwait(true);
            _saving = false;
            _UpdateState();
        }
    }

    private async Task _RecoverLiveAsync()
    {
        if (_closed || _conflict || _needsFileRefresh || Editor.Text != _savedText || _live is not { Status.RequiresRejoin: true } old)
        {
            return;
        }
        try
        {
            if (!await old.CloseAsync().ConfigureAwait(true))
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, "The old live session could not be closed. Your text is saved.");
            }
            if (_pendingLiveId is { } pending)
            {
                await _client.CloseLiveAsync(pending, _lifetime.Token).ConfigureAwait(true);
                _pendingLiveId = null;
            }
            var live = new LiveTextDocument(Editor.TextEditor.Document, _client, _logger);
            var attached = false;
            try
            {
                var (id, snapshot, wire) = await _client.OpenLiveAsync(_options.File, _lifetime.Token).ConfigureAwait(true);
                _pendingLiveId = id;
                if (_closed || Editor.Text != _savedText)
                {
                    await _client.CloseLiveAsync(id, CancellationToken.None).ConfigureAwait(true);
                    _pendingLiveId = null;
                    return;
                }
                _liveLightPending = false;
                _live = live;
                Editor.Text = snapshot.Text;
                live.Attach(id, snapshot);
                live.Changed += (_, _) => _UpdateState();
                attached = true;
                _pendingLiveId = null;
                _presence?.Dispose();
                _presence = null;
                if (wire.Peers is { } peers)
                {
                    await _ShowPresenceAsync(id, peers, wire.OpenUrl, new Snapshot(_savedText, _hasBom, _version ?? "", null, null, _canWrite)).ConfigureAwait(true);
                }
            }
            finally
            {
                if (!attached)
                {
                    live.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Reopening live file workspace {WorkspaceId} failed; its text remains saved or dirty locally.", _context.WorkspaceId);
            _error = "The live connection could not be restored. Your text is still here.";
            _retrySeconds = Math.Min(60, _retrySeconds * 2);
        }
    }

    private bool _MergeLiveLight(Snapshot theirs, string? origin = null)
    {
        var mine = Editor.Text;
        var beforeOffline = origin ?? mine;
        var shared = LineMerge.Merge(_savedText.Split('\n'), theirs.Text.Split('\n'), beforeOffline.Split('\n'), out var sharedOverlap);
        var merged = shared;
        var localOverlap = false;
        if (origin is not null)
        {
            merged = LineMerge.Merge(origin.Split('\n'), shared, mine.Split('\n'), out localOverlap);
        }
        if (sharedOverlap || localOverlap)
        {
            _theirs = theirs;
            _conflict = true;
            _conflictMine = mine;
            _conflictMerged = string.Join('\n', merged);
            return false;
        }
        _savedText = theirs.Text;
        _version = theirs.Version;
        _hasBom = theirs.HasBom;
        Editor.TextEditor.Document.Text = string.Join('\n', merged);
        return true;
    }
}
