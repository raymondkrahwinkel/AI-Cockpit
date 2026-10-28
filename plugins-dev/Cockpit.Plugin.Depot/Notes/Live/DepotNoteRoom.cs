using System.Text.Json;
using Jint;
using Jint.Native;

namespace Cockpit.Plugin.Depot.Notes.Live;

// One text change in UTF-16 characters: replace Length at Offset with Text. In a list each op applies to what the one before left.
// The shape of LB-2's host contract (AC-1898), kept inside the plugin until that contract is merged.
internal sealed record DepotNoteTextOp(int Offset, int Length, string Text);

// The room's Y.Doc and the bridge to the host's Markdown (the mirror), behind one actor: every call is queued behind the one before
// it. Two transactions at once lost updates silently in AC-1576, and a Jint engine is single-threaded anyway.
internal interface IDepotNoteRoom : IAsyncDisposable
{
    // A row or an update from the hub; once the mirror is loaded, what it changed in the host's text.
    Task<IReadOnlyList<DepotNoteTextOp>> ApplyAsync(byte[] update, CancellationToken cancellationToken);

    // What the hub lacks: this doc beyond the state of `known` (rows and early updates), or null when that is nothing.
    Task<byte[]?> MissingAsync(IReadOnlyList<byte[]> known, CancellationToken cancellationToken);

    Task<bool> IsEmptyAsync(CancellationToken cancellationToken);

    Task SeedAsync(string markdown, CancellationToken cancellationToken);

    // Protocol §5.3: the rows draw nothing while the file has text, so the room needs a Refill.
    Task<bool> NeedsRefillAsync(string markdown, CancellationToken cancellationToken);

    // Builds the mirror from the doc and returns the ops that turn `previous`, the host's text so far, into it.
    Task<IReadOnlyList<DepotNoteTextOp>> LoadAsync(string previous, CancellationToken cancellationToken);

    Task<string> ReadAsync(CancellationToken cancellationToken);

    // The host's ops, applied to the mirror and the doc as one local transaction; returns the v1 updates to send.
    Task<IReadOnlyList<byte[]>> EditAsync(IReadOnlyList<DepotNoteTextOp> ops, CancellationToken cancellationToken);

    // The whole doc as one v1 update, for Compact.
    Task<byte[]> SnapshotAsync(CancellationToken cancellationToken);
}

internal delegate IDepotNoteRoom DepotNoteRoomFactory(uint clientId);

// One engine per room, loaded on the first call: the engine and the bundle take about half a second, and none of it belongs in
// plugin load or in Cockpit's start.
internal sealed class JintNoteRoom(uint clientId) : IDepotNoteRoom
{
    private const string Bundle = "Cockpit.Plugin.Depot.Notes.Live.Bundle.room.bundle.js";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ponytail: one engine per room (about 25 MiB each); one shared engine with several docs when many rooms are open at once.
    private readonly SemaphoreSlim _actor = new(1, 1);
    private Engine? _engine;
    private JsValue? _room;

    public async Task<IReadOnlyList<DepotNoteTextOp>> ApplyAsync(byte[] update, CancellationToken cancellationToken) =>
        _Ops(await _CallAsync("apply", cancellationToken, Convert.ToBase64String(update)).ConfigureAwait(false));

    public async Task<byte[]?> MissingAsync(IReadOnlyList<byte[]> known, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(known.Select(Convert.ToBase64String));
        var diff = Convert.FromBase64String(await _CallAsync("missing", cancellationToken, json).ConfigureAwait(false));

        // An update with no structs and no deletes is two bytes (0, 0).
        return diff.Length <= 2 ? null : diff;
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken) =>
        await _CallAsync("isEmpty", cancellationToken).ConfigureAwait(false) == "true";

    public Task SeedAsync(string markdown, CancellationToken cancellationToken) => _CallAsync("seed", cancellationToken, markdown);

    public async Task<bool> NeedsRefillAsync(string markdown, CancellationToken cancellationToken) =>
        await _CallAsync("needsRefill", cancellationToken, markdown).ConfigureAwait(false) == "true";

    public async Task<IReadOnlyList<DepotNoteTextOp>> LoadAsync(string previous, CancellationToken cancellationToken) =>
        _Ops(await _CallAsync("load", cancellationToken, previous).ConfigureAwait(false));

    public Task<string> ReadAsync(CancellationToken cancellationToken) => _CallAsync("text", cancellationToken);

    public async Task<IReadOnlyList<byte[]>> EditAsync(IReadOnlyList<DepotNoteTextOp> ops, CancellationToken cancellationToken)
    {
        var updates = await _CallAsync("edit", cancellationToken, JsonSerializer.Serialize(ops, Json)).ConfigureAwait(false);
        return [.. (JsonSerializer.Deserialize<string[]>(updates) ?? []).Select(Convert.FromBase64String)];
    }

    public async Task<byte[]> SnapshotAsync(CancellationToken cancellationToken) =>
        Convert.FromBase64String(await _CallAsync("snapshot", cancellationToken).ConfigureAwait(false));

    public ValueTask DisposeAsync()
    {
        _engine?.Dispose();
        _actor.Dispose();
        return ValueTask.CompletedTask;
    }

    // A script in the room's engine, where the bundle is `DepotRoom`: how the tests reach the converter and the doc (DepotRoom.current()).
    internal Task<string> EvaluateAsync(string script, CancellationToken cancellationToken) =>
        _InActorAsync((engine, _) => engine.Evaluate(script).ToString(), cancellationToken);

    private Task<string> _CallAsync(string function, CancellationToken cancellationToken, params object[] arguments) =>
        _InActorAsync((engine, room) => engine.Invoke(room.AsObject().Get(function), arguments).ToString(), cancellationToken);

    private async Task<string> _InActorAsync(Func<Engine, JsValue, string> call, CancellationToken cancellationToken)
    {
        await _actor.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var (engine, room) = _Room();
                return call(engine, room);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actor.Release();
        }
    }

    private static IReadOnlyList<DepotNoteTextOp> _Ops(string json) => JsonSerializer.Deserialize<DepotNoteTextOp[]>(json, Json) ?? [];

    private (Engine Engine, JsValue Room) _Room()
    {
        if (_engine is { } loaded && _room is { } ready)
        {
            return (loaded, ready);
        }

        using var stream = typeof(JintNoteRoom).Assembly.GetManifestResourceStream(Bundle)
            ?? throw new InvalidOperationException("The room bundle is not part of this plugin.");
        using var reader = new StreamReader(stream);
        var engine = new Engine();

        // lib0 (under yjs) reads crypto.getRandomValues at load; Jint has no Web Crypto. Only ids use it, and the client id is set below.
        engine.Execute("globalThis.crypto = { subtle: {}, getRandomValues: (a) => { for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 4294967296); return a; } };");
        engine.Execute(reader.ReadToEnd());
        var room = engine.GetValue("DepotRoom");
        engine.Invoke(room.AsObject().Get("createRoom"), (double)clientId);
        _engine = engine;
        _room = room;
        return (engine, room);
    }
}
