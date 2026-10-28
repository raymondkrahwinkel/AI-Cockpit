using System.Text.Json;
using Jint;
using Jint.Native;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The room's Y.Doc, behind one actor: every call is queued behind the one before it. Two transactions at once lost updates
// silently in AC-1576, and a Jint engine is single-threaded anyway.
internal interface IDepotNoteRoom : IAsyncDisposable
{
    Task ApplyAsync(byte[] update, CancellationToken cancellationToken);

    // What the hub lacks: this doc beyond the state of `known` (rows and early updates), or null when that is nothing.
    Task<byte[]?> MissingAsync(IReadOnlyList<byte[]> known, CancellationToken cancellationToken);

    Task<DepotNoteText> ReadAsync(CancellationToken cancellationToken);
}

internal sealed record DepotNoteText(string Fragment, string Frontmatter);

internal delegate IDepotNoteRoom DepotNoteRoomFactory(uint clientId);

// One engine per room, loaded on the first call: the engine and the bundle take about half a second, and none of it belongs in
// plugin load or in Cockpit's start.
internal sealed class JintNoteRoom(uint clientId) : IDepotNoteRoom
{
    private const string Bundle = "Cockpit.Plugin.Depot.Notes.Live.Bundle.room.bundle.js";

    // ponytail: one engine per room (about 25 MiB each); one shared engine with several docs when many rooms are open at once.
    private readonly SemaphoreSlim _actor = new(1, 1);
    private Engine? _engine;
    private JsValue? _room;

    public Task ApplyAsync(byte[] update, CancellationToken cancellationToken) =>
        _CallAsync("apply", cancellationToken, Convert.ToBase64String(update));

    public async Task<byte[]?> MissingAsync(IReadOnlyList<byte[]> known, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(known.Select(Convert.ToBase64String));
        var diff = Convert.FromBase64String(await _CallAsync("missing", cancellationToken, json).ConfigureAwait(false));

        // An update with no structs and no deletes is two bytes (0, 0).
        return diff.Length <= 2 ? null : diff;
    }

    public async Task<DepotNoteText> ReadAsync(CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await _CallAsync("read", cancellationToken).ConfigureAwait(false));
        return new DepotNoteText(
            document.RootElement.GetProperty("fragment").GetString() ?? string.Empty,
            document.RootElement.GetProperty("frontmatter").GetString() ?? string.Empty);
    }

    public ValueTask DisposeAsync()
    {
        _engine?.Dispose();
        _actor.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> _CallAsync(string function, CancellationToken cancellationToken, params object[] arguments)
    {
        await _actor.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var (engine, room) = _Room();
                return engine.Invoke(room.AsObject().Get(function), arguments).ToString();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actor.Release();
        }
    }

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
