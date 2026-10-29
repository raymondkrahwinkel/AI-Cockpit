using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Cockpit.Core.Markdown;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

// Keeps one TextDocument in step with a live source: local changes and undos go out in order, one call at a time;
// patches are shifted past the ops the source has not applied yet. A gap or a broken patch is resynced.
// Undo is the source's, so AvaloniaEdit's own stack is off: it would also undo the patches of others.
internal sealed class LiveTextDocument : IDisposable
{
    private readonly TextDocument _document;
    private readonly FileSourceChannelClient _client;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Outgoing> _queue = [];
    private readonly Dictionary<long, LiveFilePatch> _early = [];
    private readonly List<JsonElement> _beforeOpen = [];
    private readonly StringBuilder _base = new();
    private readonly IDisposable _patches;
    private readonly IDisposable _states;
    private string? _id;
    private List<TextOp>? _inflight;
    private string _editId = "";
    private long _inflightBase;
    private long? _staleAt;
    private long _rev;
    private bool _sending;
    private bool _resync;
    private bool _reading;
    private bool _failed;
    private bool _remote;
    private bool _closed;
    private TaskCompletionSource<bool>? _idle;

    // Subscribes before the session is opened, so no patch published between the open and Attach is lost.
    public LiveTextDocument(TextDocument document, FileSourceChannelClient client, ILogger logger)
    {
        _document = document;
        _client = client;
        _logger = logger;
        Status = new LiveFileStatus(LiveFileState.Connecting);
        _patches = client.Subscribe(FileSourceChannel.LivePatch, evt => Dispatcher.UIThread.Post(() => _OnPatch(evt.Payload)));
        _states = client.Subscribe(FileSourceChannel.LiveState, evt => Dispatcher.UIThread.Post(() => _OnState(evt.Payload)));
    }

    public LiveFileStatus Status { get; private set; }

    public bool Failed => _failed;
    public bool HasUnsentText => Status.UnsentSourceText is not null || _inflight is { Count: > 0 } || _queue.Any(item => item.Ops.Count > 0);
    public string SourceText => Status.UnsentSourceText ?? _base.ToString();

    public event EventHandler? Changed;

    public event EventHandler<ExternalTextEdit>? ExternalEdited;

    private bool _IsIdle => !_sending && _inflight is null && _queue.Count == 0 && !_resync;

    // The document already holds the snapshot's text.
    public void Attach(string sessionId, LiveFileSnapshot snapshot)
    {
        _id = sessionId;
        _rev = snapshot.Rev;
        _base.Clear().Append(snapshot.Text);
        Status = snapshot.Status;
        _document.UndoStack.SizeLimit = 0;
        _document.Changed += _OnChanged;
        foreach (var payload in _beforeOpen)
        {
            _OnPatch(payload);
        }
        _beforeOpen.Clear();
    }

    public void Undo(bool redo)
    {
        if (Status.State == LiveFileState.ReadOnly)
        {
            return;
        }
        _queue.Add(new Outgoing([], redo));
        _Pump();
    }

    public void Resync()
    {
        _resync = true;
        _failed = false;
        _Pump();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // True once every local change has reached the source; false when sending failed and the text is only here.
    public Task<bool> FlushAsync()
    {
        if (Status.State == LiveFileState.ReadOnly && HasUnsentText)
        {
            return Task.FromResult(false);
        }
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
        if (_id is { } id)
        {
            // Closing cannot be awaited by the workspace's close; a failure only leaves the source to time the session out.
            _ = _CloseAsync(id);
        }
    }

    private async Task _CloseAsync(string id)
    {
        try
        {
            await _client.CloseLiveAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Closing live file session {SessionId} failed.", id);
        }
    }

    private void _OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_remote)
        {
            return;
        }
        if (_queue.Count == 0 || _queue[^1].Redo is not null)
        {
            _queue.Add(new Outgoing([], null));
        }
        _queue[^1].Ops.Add(new TextOp(e.Offset, e.RemovalLength, e.InsertedText.Text));
        _failed = false;
        _Pump();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _OnState(JsonElement payload)
    {
        try
        {
            if (_closed || _id is null || payload.Deserialize<FileSourceLiveState>(FileSourceChannel.Json) is not { } state
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
        if (_closed)
        {
            return;
        }
        if (_id is null)
        {
            _beforeOpen.Add(payload.Clone());
            return;
        }
        try
        {
            if (payload.Deserialize<FileSourceLivePatch>(FileSourceChannel.Json) is not { } patch || patch.SessionId != _id)
            {
                return;
            }
            if (patch.Ops is null || patch.Ops.Any(op => op?.Text is null))
            {
                throw new JsonException("A live patch without ops.");
            }
            if (patch.Rev > _rev)
            {
                _early[patch.Rev] = new LiveFilePatch(patch.Rev, patch.Ops, patch.Who) { ExternalEdit = patch.ExternalEdit };
            }
            _Drain();
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "A live patch for session {SessionId} could not be applied; resyncing.", _id);
            _resync = true;
        }
        _Pump();
    }

    // Applies the patches that follow on from _rev. Patches arrive in order, so a gap with no call out to explain it is resynced.
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
            _Requeue();
        }
        _resync |= _early.Count > 0 && !_sending;
    }

    private bool _Apply(LiveFilePatch patch)
    {
        var remote = _Primitives(patch.Ops);
        if (!_Fits(remote, _base.Length))
        {
            return false;
        }
        var inflight = _inflight is null ? null : _Primitives(_inflight);
        if (inflight is not null)
        {
            (remote, inflight) = _Transform(remote, inflight);
        }
        var queued = new List<List<Prim>>();
        foreach (var item in _queue)
        {
            (remote, var mine) = _Transform(remote, _Primitives(item.Ops));
            queued.Add(mine);
        }
        if (!_Fits(remote, _document.TextLength))
        {
            return false;
        }
        foreach (var op in patch.Ops)
        {
            _base.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
        }
        _ApplyToDocument(_TextOps(remote));
        if (inflight is not null)
        {
            _inflight = _TextOps(inflight);
        }
        for (var i = 0; i < _queue.Count; i++)
        {
            _queue[i] = _queue[i] with { Ops = _TextOps(queued[i]) };
        }
        _rev = patch.Rev;
        if (patch.ExternalEdit is { } external)
        {
            var ranges = _ExternalLines(external.Ranges);
            foreach (var op in (_inflight ?? []).Concat(_queue.SelectMany(item => item.Ops)))
            {
                ranges = ranges.Where(range => op.Offset > range.End || op.Offset + op.Length < range.Start)
                    .Select(range => op.Offset < range.Start ? new LiveRange(range.Start + op.Text.Length - op.Length, range.End + op.Text.Length - op.Length) : range).ToList();
            }
            ExternalEdited?.Invoke(this, external with { Ranges = ranges });
        }
        return true;
    }

    private List<LiveRange> _ExternalLines(IReadOnlyList<LiveRange> ranges)
    {
        var text = _base.ToString();
        var lines = new List<LiveRange>();
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.End <= range.Start || range.End > text.Length)
            {
                continue;
            }
            var start = range.Start == 0 ? 0 : text.LastIndexOf('\n', range.Start - 1) + 1;
            while (start < range.End)
            {
                var end = text.IndexOf('\n', start);
                end = end < 0 ? text.Length : end;
                lines.Add(new LiveRange(start, end));
                start = end + 1;
            }
        }
        return lines;
    }

    // A replace as a delete then an insert at the same spot; the transforms below only meet these two kinds.
    private readonly record struct Prim(int Offset, int Length, string Text);

    private static List<Prim> _Primitives(IEnumerable<TextOp> ops)
    {
        var prims = new List<Prim>();
        foreach (var op in ops)
        {
            if (op.Length > 0)
            {
                prims.Add(new Prim(op.Offset, op.Length, ""));
            }
            if (op.Text.Length > 0)
            {
                prims.Add(new Prim(op.Offset, 0, op.Text));
            }
        }
        return prims;
    }

    private static List<TextOp> _TextOps(List<Prim> prims) => [.. prims.Select(p => new TextOp(p.Offset, p.Length, p.Text))];

    private static bool _Fits(List<Prim> prims, int length)
    {
        foreach (var p in prims)
        {
            if (p.Offset < 0 || p.Length < 0 || p.Offset + p.Length > length)
            {
                return false;
            }
            length += p.Text.Length - p.Length;
        }
        return true;
    }

    // Both lists apply to the same text. Returns the remote list to apply after the local one, and the local list to
    // apply after the remote one; both orders give the same text. Concurrent inserts survive a delete around them.
    private static (List<Prim> Remote, List<Prim> Local) _Transform(List<Prim> remote, List<Prim> local)
    {
        if (remote.Count == 0 || local.Count == 0)
        {
            return (remote, local);
        }
        if (remote.Count > 1)
        {
            var (first, afterFirst) = _Transform([remote[0]], local);
            var (rest, afterRest) = _Transform(remote.GetRange(1, remote.Count - 1), afterFirst);
            return ([.. first, .. rest], afterRest);
        }
        if (local.Count > 1)
        {
            var (overFirst, first) = _Transform(remote, [local[0]]);
            var (overRest, rest) = _Transform(overFirst, local.GetRange(1, local.Count - 1));
            return (overRest, [.. first, .. rest]);
        }
        var (r, l) = (remote[0], local[0]);
        if (r.Length == 0 && l.Length == 0)
        {
            // Two inserts at one spot: the remote one goes first.
            return r.Offset <= l.Offset
                ? ([r], [l with { Offset = l.Offset + r.Text.Length }])
                : ([r with { Offset = r.Offset + l.Text.Length }], [l]);
        }
        if (r.Length == 0)
        {
            var (insert, delete) = _InsertOverDelete(r, l);
            return (insert, delete);
        }
        if (l.Length == 0)
        {
            var (insert, delete) = _InsertOverDelete(l, r);
            return (delete, insert);
        }
        var overlap = Math.Max(0, Math.Min(r.Offset + r.Length, l.Offset + l.Length) - Math.Max(r.Offset, l.Offset));
        return (_Delete(_Map(r.Offset, l), r.Length - overlap), _Delete(_Map(l.Offset, r), l.Length - overlap));
    }

    // An insert against a concurrent delete: the insert lands where the deleted text was, and the delete skips it.
    private static (List<Prim> Insert, List<Prim> Delete) _InsertOverDelete(Prim insert, Prim delete)
    {
        var end = delete.Offset + delete.Length;
        if (insert.Offset <= delete.Offset)
        {
            return ([insert], [delete with { Offset = delete.Offset + insert.Text.Length }]);
        }
        if (insert.Offset >= end)
        {
            return ([insert with { Offset = insert.Offset - delete.Length }], [delete]);
        }
        return ([insert with { Offset = delete.Offset }],
            [.. _Delete(delete.Offset, insert.Offset - delete.Offset), .. _Delete(delete.Offset + insert.Text.Length, end - insert.Offset)]);
    }

    private static int _Map(int offset, Prim delete) =>
        offset <= delete.Offset ? offset : offset >= delete.Offset + delete.Length ? offset - delete.Length : delete.Offset;

    private static List<Prim> _Delete(int offset, int length) => length > 0 ? [new Prim(offset, length, "")] : [];

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

    private void _Requeue()
    {
        if (_inflight is { Count: > 0 } ops)
        {
            _queue.Insert(0, new Outgoing(ops, null));
        }
        _inflight = null;
        _staleAt = null;
    }

    private void _Pump()
    {
        if (_sending || _closed || _failed || _id is null)
        {
            return;
        }
        if (Status.State == LiveFileState.ReadOnly && !_resync)
        {
            _idle?.TrySetResult(!HasUnsentText);
            _idle = null;
            return;
        }
        if (_inflight is not null && _staleAt is null && Status.State != LiveFileState.ReadOnly)
        {
            // An edit whose answer failed goes again first, under its own id, so the source says whether it landed.
            _Send(_EditAsync);
        }
        else if (_resync)
        {
            _resync = false;
            _Send(_ResyncAsync);
        }
        else if (_inflight is not null)
        {
            // A stale edit waits for the patches it missed.
        }
        else if (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (next.Redo is { } redo)
            {
                _Send(ct => _UndoAsync(redo, ct));
            }
            else
            {
                _inflight = next.Ops;
                _Send(_EditAsync);
            }
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
            // The text stays here; the edit goes again on the next change, status or close, under its own id.
            _logger.LogWarning(error, "Live file session {SessionId} could not reach its source.", _id);
            _failed = true;
            _idle?.TrySetResult(false);
            _idle = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        _Pump();
    }

    private async Task _UndoAsync(bool redo, CancellationToken cancellationToken)
    {
        try
        {
            await _client.UndoLiveAsync(_id ?? "", redo, cancellationToken).ConfigureAwait(true);
        }
        catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.Forbidden)
        {
            _ReadOnly();
        }
        catch (FileSourceException error)
        {
            _logger.LogWarning(error, "Undo in live file session {SessionId} failed.", _id);
        }
    }

    private async Task _EditAsync(CancellationToken cancellationToken)
    {
        var ops = _inflight ?? [];
        if (_editId.Length == 0 || _inflightBase != _rev)
        {
            _editId = Guid.NewGuid().ToString("n");
            _inflightBase = _rev;
        }
        var baseRev = _rev;
        LiveEditResult result;
        try
        {
            result = await _client.EditLiveAsync(_id ?? "", _editId, baseRev, ops, cancellationToken).ConfigureAwait(true);
        }
        catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.Forbidden)
        {
            _Requeue();
            _ReadOnly();
            return;
        }
        if (result.Status == LiveEditStatus.Applied && result.Rev == baseRev + 1 && _rev == baseRev)
        {
            foreach (var op in ops)
            {
                _base.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
            }
            _rev = result.Rev;
            _inflight = null;
            _editId = "";
        }
        else if (result.Status == LiveEditStatus.Stale && result.Rev > baseRev)
        {
            _staleAt = result.Rev;
            _editId = "";
        }
        else
        {
            _logger.LogWarning("Live file session {SessionId} answered edit {Rev} with {Status} {Answer}; resyncing.", _id, baseRev, result.Status, result.Rev);
            _editId = "";
            _Requeue();
            _resync = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _ReadOnly()
    {
        Status = new LiveFileStatus(LiveFileState.ReadOnly, "You can no longer edit this note.") { ReadOnlyReason = LiveReadOnlyReason.RoleLost };
        _failed = HasUnsentText;
        _idle?.TrySetResult(!HasUnsentText);
        _idle = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The whole text again, merged per line with what is typed here and not yet sent. Only reached on a gap, a broken
    // patch or a Stale status, never with an edit whose fate is unknown.
    private async Task _ResyncAsync(CancellationToken cancellationToken)
    {
        if (_inflight is not null)
        {
            _Requeue();
        }
        _reading = true;
        LiveFileSnapshot snapshot;
        try
        {
            (_, snapshot) = await _client.ReadLiveAsync(_id ?? "", cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _reading = false;
        }
        var mine = _document.Text;
        var theirs = snapshot.Text;
        var unsent = _queue.Any(item => item.Ops.Count > 0);
        var merged = !unsent ? theirs
            : string.Join('\n', LineMerge.Merge(_base.ToString().Split('\n'), theirs.Split('\n'), mine.Split('\n')));
        if (_Diff(mine, merged) is { } toDocument)
        {
            _ApplyToDocument([toDocument]);
        }
        _base.Clear().Append(theirs);
        _rev = snapshot.Rev;
        var undos = _queue.Where(item => item.Redo is not null).ToList();
        _queue.Clear();
        if (_Diff(theirs, merged) is { } toSource)
        {
            _queue.Add(new Outgoing([toSource], null));
        }
        _queue.AddRange(undos);
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

    // An edit (Redo null) or an undo (Redo false) or redo (Redo true), sent in the order they happened.
    private sealed record Outgoing(List<TextOp> Ops, bool? Redo);
}
