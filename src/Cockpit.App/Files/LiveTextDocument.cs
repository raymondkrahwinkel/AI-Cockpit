using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Cockpit.Core.Markdown;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

// Keeps one TextDocument in step with a live source: local changes go out as ops, one edit in flight at a time;
// patches are shifted past the ops the source has not applied yet; anything that does not fit is resynced.
// Undo is the source's, so AvaloniaEdit's own stack is off: it would also undo the patches of others.
internal sealed class LiveTextDocument : IDisposable
{
    private readonly TextDocument _document;
    private readonly FileSourceChannelClient _client;
    private readonly string _id;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TextOp> _buffer = [];
    private readonly Queue<bool> _undos = new();
    private readonly Dictionary<long, LiveFilePatch> _early = [];
    private readonly StringBuilder _base;
    private readonly IDisposable _patches;
    private readonly IDisposable _states;
    private List<TextOp>? _inflight;
    private long? _staleAt;
    private long _rev;
    private bool _sending;
    private bool _resync;
    private bool _reading;
    private bool _failed;
    private bool _remote;
    private bool _closed;
    private TaskCompletionSource<bool>? _idle;

    public LiveTextDocument(TextDocument document, FileSourceChannelClient client, string sessionId, LiveFileSnapshot snapshot, ILogger logger)
    {
        _document = document;
        _client = client;
        _id = sessionId;
        _logger = logger;
        _rev = snapshot.Rev;
        _base = new StringBuilder(snapshot.Text);
        Status = snapshot.Status;
        document.UndoStack.SizeLimit = 0;
        document.Changed += _OnChanged;
        _patches = client.Subscribe(FileSourceChannel.LivePatch, evt => Dispatcher.UIThread.Post(() => _OnPatch(evt.Payload)));
        _states = client.Subscribe(FileSourceChannel.LiveState, evt => Dispatcher.UIThread.Post(() => _OnState(evt.Payload)));
    }

    public LiveFileStatus Status { get; private set; }

    public event EventHandler? Changed;

    private bool _IsIdle => !_sending && _inflight is null && _buffer.Count == 0 && _undos.Count == 0 && !_resync;

    public void Undo(bool redo)
    {
        _undos.Enqueue(redo);
        _Pump();
    }

    public void Resync()
    {
        _resync = true;
        _Pump();
    }

    // True once every local change has reached the source; false when sending failed and the text is only here.
    public Task<bool> FlushAsync()
    {
        if (_closed || _IsIdle)
        {
            return Task.FromResult(!_closed);
        }
        _idle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = _idle.Task;
        if (_failed)
        {
            _failed = false;
            _Pump();
        }
        return idle;
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _document.Changed -= _OnChanged;
        _patches.Dispose();
        _states.Dispose();
        _lifetime.Cancel();
        _idle?.TrySetResult(false);
        // Closing cannot be awaited by the workspace's close; a failure only leaves the source to time the session out.
        _ = _CloseAsync();
    }

    private async Task _CloseAsync()
    {
        try
        {
            await _client.CloseLiveAsync(_id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Closing live file session {SessionId} failed.", _id);
        }
    }

    private void _OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_remote)
        {
            return;
        }
        _buffer.Add(new TextOp(e.Offset, e.RemovalLength, e.InsertedText.Text));
        _failed = false;
        _Pump();
    }

    private void _OnState(JsonElement payload)
    {
        try
        {
            if (_closed || payload.Deserialize<FileSourceLiveState>(FileSourceChannel.Json) is not { } state
                || state.SessionId != _id || state.Status is null)
            {
                return;
            }
            Status = state.Status;
            _failed = false;
            _resync |= state.Status.State == LiveFileState.Stale;
            Changed?.Invoke(this, EventArgs.Empty);
            _Pump();
        }
        catch (JsonException error)
        {
            _logger.LogWarning(error, "A live file state for session {SessionId} could not be read.", _id);
        }
    }

    private void _OnPatch(JsonElement payload)
    {
        FileSourceLivePatch? patch;
        try
        {
            patch = payload.Deserialize<FileSourceLivePatch>(FileSourceChannel.Json);
        }
        catch (JsonException error)
        {
            _logger.LogWarning(error, "A live patch for session {SessionId} could not be read; resyncing.", _id);
            Resync();
            return;
        }
        if (_closed || patch is null || patch.SessionId != _id)
        {
            return;
        }
        if (patch.Ops is null || patch.Ops.Any(op => op?.Text is null))
        {
            Resync();
            return;
        }
        if (patch.Rev > _rev)
        {
            _early[patch.Rev] = new LiveFilePatch(patch.Rev, patch.Ops, patch.Who);
        }
        _Drain();
        _Pump();
    }

    // Applies the patches that follow on from _rev; a gap with nothing out to explain it is resynced.
    private void _Drain()
    {
        if (_reading)
        {
            return;
        }
        while (_early.Remove(_rev + 1, out var next))
        {
            if (!_Apply(next))
            {
                _resync = true;
                break;
            }
        }
        foreach (var old in _early.Keys.Where(rev => rev <= _rev).ToList())
        {
            _early.Remove(old);
        }
        if (_inflight is not null && !_sending && _staleAt is { } at && _rev >= at)
        {
            _buffer.InsertRange(0, _inflight);
            _inflight = null;
            _staleAt = null;
        }
        _resync |= _early.Count > 0 && !_sending && _inflight is null;
    }

    private bool _Apply(LiveFilePatch patch)
    {
        var pending = new List<TextOp>([.. _inflight ?? [], .. _buffer]);
        var local = new List<TextOp>();
        int baseLength = _base.Length, docLength = _document.TextLength;
        foreach (var op in patch.Ops)
        {
            if (op.Offset < 0 || op.Length < 0 || op.Offset + op.Length > baseLength)
            {
                return false;
            }
            baseLength += op.Text.Length - op.Length;
            var mapped = op;
            for (var i = 0; i < pending.Count; i++)
            {
                var mine = pending[i];
                if (!_Transform(ref mapped, ref mine))
                {
                    return false;
                }
                pending[i] = mine;
            }
            if (mapped.Offset + mapped.Length > docLength)
            {
                return false;
            }
            docLength += mapped.Text.Length - mapped.Length;
            local.Add(mapped);
        }
        foreach (var op in patch.Ops)
        {
            _base.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
        }
        _ApplyToDocument(local);
        var sent = _inflight?.Count ?? 0;
        if (_inflight is not null)
        {
            _inflight = pending.GetRange(0, sent);
        }
        _buffer.Clear();
        _buffer.AddRange(pending.Skip(sent));
        _rev = patch.Rev;
        return true;
    }

    // Moves a remote op past a local one the source has not seen, and the local one past it. An insert at the same
    // spot goes before the local one. Overlapping ranges have no single answer: false, and the caller resyncs.
    private static bool _Transform(ref TextOp remote, ref TextOp local)
    {
        if (remote.Offset + remote.Length <= local.Offset)
        {
            local = local with { Offset = local.Offset + remote.Text.Length - remote.Length };
            return true;
        }
        if (local.Offset + local.Length <= remote.Offset)
        {
            remote = remote with { Offset = remote.Offset + local.Text.Length - local.Length };
            return true;
        }
        return false;
    }

    // Replace, not Text =: the caret and selection are anchored and stay on their characters.
    private void _ApplyToDocument(IReadOnlyList<TextOp> ops)
    {
        _remote = true;
        _document.BeginUpdate();
        try
        {
            foreach (var op in ops)
            {
                _document.Replace(op.Offset, op.Length, op.Text);
            }
        }
        finally
        {
            _document.EndUpdate();
            _remote = false;
        }
    }

    private void _Pump()
    {
        if (_sending || _closed || _failed)
        {
            return;
        }
        if (_resync)
        {
            _resync = false;
            _Send(_ResyncAsync);
        }
        else if (_inflight is not null)
        {
            // A stale edit waits for the patches it missed.
        }
        else if (_buffer.Count > 0)
        {
            _inflight = [.. _buffer];
            _buffer.Clear();
            _Send(_EditAsync);
        }
        else if (_undos.TryDequeue(out var redo))
        {
            _Send(ct => _client.UndoLiveAsync(_id, redo, ct));
        }
        else
        {
            _idle?.TrySetResult(true);
            _idle = null;
        }
    }

    private void _Send(Func<CancellationToken, Task> call)
    {
        _sending = true;
        _ = _RunAsync(call);
    }

    private async Task _RunAsync(Func<CancellationToken, Task> call)
    {
        try
        {
            try
            {
                await call(_lifetime.Token).ConfigureAwait(true);
            }
            finally
            {
                _sending = false;
            }
            _Drain();
        }
        catch (OperationCanceledException) when (_closed)
        {
            return;
        }
        catch (Exception error)
        {
            // The edit may or may not have landed: keep the text here and resync, which merges it either way.
            _logger.LogWarning(error, "Live file session {SessionId} could not reach its source.", _id);
            if (_inflight is not null)
            {
                _buffer.InsertRange(0, _inflight);
                _inflight = null;
                _staleAt = null;
            }
            _resync = true;
            _failed = true;
            _idle?.TrySetResult(false);
            _idle = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        _Pump();
    }

    public bool Failed => _failed;

    private async Task _EditAsync(CancellationToken cancellationToken)
    {
        var baseRev = _rev;
        var ops = _inflight ?? [];
        var result = await _client.EditLiveAsync(_id, baseRev, ops, cancellationToken).ConfigureAwait(true);
        if (result.Status == LiveEditStatus.Applied && result.Rev == baseRev + 1 && _rev == baseRev)
        {
            foreach (var op in ops)
            {
                _base.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
            }
            _rev = result.Rev;
            _inflight = null;
        }
        else if (result.Status == LiveEditStatus.Stale && result.Rev > baseRev)
        {
            _staleAt = result.Rev;
        }
        else
        {
            throw new InvalidOperationException($"The source answered edit {baseRev} with {result.Status} {result.Rev}.");
        }
    }

    // The whole text again, merged per line with what is typed here and not yet sent, so no local text is lost.
    private async Task _ResyncAsync(CancellationToken cancellationToken)
    {
        if (_inflight is not null)
        {
            _buffer.InsertRange(0, _inflight);
            _inflight = null;
            _staleAt = null;
        }
        // Patches that arrive meanwhile wait in _early; the snapshot may already hold them.
        _reading = true;
        LiveFileSnapshot snapshot;
        try
        {
            (_, snapshot) = await _client.ReadLiveAsync(_id, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _reading = false;
        }
        var mine = _document.Text;
        var theirs = snapshot.Text;
        var merged = _buffer.Count == 0 ? theirs
            : string.Join('\n', LineMerge.Merge(_base.ToString().Split('\n'), theirs.Split('\n'), mine.Split('\n')));
        if (_Diff(mine, merged) is { } toDocument)
        {
            _ApplyToDocument([toDocument]);
        }
        _base.Clear().Append(theirs);
        _rev = snapshot.Rev;
        _buffer.Clear();
        if (_Diff(theirs, merged) is { } toSource)
        {
            _buffer.Add(toSource);
        }
        if (Status != snapshot.Status)
        {
            Status = snapshot.Status;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TextOp? _Diff(string from, string to)
    {
        var prefix = 0;
        var max = Math.Min(from.Length, to.Length);
        while (prefix < max && from[prefix] == to[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < max - prefix && from[from.Length - 1 - suffix] == to[to.Length - 1 - suffix])
        {
            suffix++;
        }
        return prefix == from.Length && prefix == to.Length ? null
            : new TextOp(prefix, from.Length - prefix - suffix, to[prefix..(to.Length - suffix)]);
    }
}
