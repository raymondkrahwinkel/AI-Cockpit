using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cockpit.Plugin.Depot.Notes.Live;

// Docs/notes-hub-protocol.md §3. Data is Yjs v1 and nothing else (§5.1).
internal sealed record DepotNoteJoinRequest(string Org, string Project, string Path, bool Watch, string? Epoch, string? Schema, bool Save);

internal sealed record DepotNoteRow(long Seq, byte[] Data);

internal sealed record DepotNoteJoinResult(
    string DocumentId, string Role, IReadOnlyList<DepotNoteRow> Updates, bool Seed, bool AwaitingSeed, string? Epoch, bool Stale,
    bool SchemaMismatch, string? Saver = null, string Markdown = "", int CompactAfter = DepotNoteJoinResult.DefaultCompactAfter)
{
    // Protocol §7, for a hub that leaves the field out.
    public const int DefaultCompactAfter = 200;
}

// Args are the event's arguments as the hub sends them (§4); `update` is (documentId, byte[]), `refused` is (code) alone.
internal sealed record DepotNoteEvent(string Kind, object?[] Args)
{
    public string? DocumentId => Kind == "refused" ? null : Args.Length > 0 ? Args[0] as string : null;
}

internal interface IDepotNotesHub : IAsyncDisposable
{
    // Before StartAsync and Join: the hub can send events before the Join result is back (§3 step 1).
    void Subscribe(Func<DepotNoteEvent, Task> onEvent);

    Task StartAsync(CancellationToken cancellationToken);

    Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken);

    Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken);

    // A person typed: before the Update that carries it, or the room is never seen as dirty and never saved (protocol §6).
    Task EditedAsync(string documentId, CancellationToken cancellationToken);

    Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken);

    // True when the room is refilled from the file; a `reset` then follows.
    Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken);
}

internal delegate IDepotNotesHub DepotNotesHubFactory(DepotConnectionRegistration connection, string userId, Func<Exception?, Task> onClosed);

internal sealed class SignalRDepotNotesHub : IDepotNotesHub
{
    // Names and argument types of every event the hub sends (§4), all registered so a later client reads what this one ignores.
    private static readonly (string Name, Type[] Types)[] _events =
    [
        ("update", [typeof(string), typeof(byte[])]),
        ("presence", [typeof(string), typeof(string), typeof(byte[])]),
        ("left", [typeof(string), typeof(string)]),
        ("saver", [typeof(string), typeof(string), typeof(bool), typeof(string)]),
        ("edited", [typeof(string)]),
        ("live", [typeof(string), typeof(bool)]),
        ("finished", [typeof(string), typeof(JsonElement), typeof(string)]),
        ("external", [typeof(string), typeof(string), typeof(string)]),
        ("outside", [typeof(string), typeof(JsonElement)]),
        ("merged", [typeof(string), typeof(int[]), typeof(int)]),
        ("reset", [typeof(string)]),
        ("refused", [typeof(string)]),
    ];

    private readonly HubConnection _connection;

    public SignalRDepotNotesHub(DepotConnectionRegistration connection, string userId, Func<HttpMessageHandler> handler, Func<Exception?, Task> onClosed)
    {
        // Server-sent events: SignalR never hands a WebSocket upgrade to this handler (measured: 403, no bearer), while the stream and
        // every send of SSE go through it (AC-1905). No Origin, no cookie: the gate takes the bearer.
        // No automatic reconnect either: the new connection has no Join, so the session joins again itself.
        _connection = new HubConnectionBuilder()
            .WithUrl(
                $"{connection.Url.TrimEnd('/')}/hubs/notes?expectedUser={Uri.EscapeDataString(userId)}",
                options =>
                {
                    options.Transports = HttpTransportType.ServerSentEvents;
                    options.HttpMessageHandlerFactory = _ => handler();
                })
            .Build();
        _connection.Closed += onClosed;
    }

    // The client runs one handler at a time, in the order the events came, so updates reach the room in order.
    public void Subscribe(Func<DepotNoteEvent, Task> onEvent)
    {
        foreach (var (name, types) in _events)
        {
            _connection.On(name, types, args => onEvent(new DepotNoteEvent(name, args)));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    public async Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
    {
        var answer = await _connection.InvokeAsync<JsonElement>("Join", request, cancellationToken).ConfigureAwait(false);
        string? Text(string name) => answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string name) => answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        List<DepotNoteRow> rows = [];
        if (answer.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in updates.EnumerateArray())
            {
                rows.Add(new DepotNoteRow(row.GetProperty("seq").GetInt64(), row.GetProperty("data").GetBytesFromBase64()));
            }
        }

        return new DepotNoteJoinResult(
            Text("documentId") ?? string.Empty, Text("role") ?? "viewer", rows, Flag("seed"), Flag("awaitingSeed"), Text("epoch"), Flag("stale"),
            Flag("schemaMismatch"), Text("saver"), Text("markdown") ?? string.Empty,
            answer.TryGetProperty("compactAfter", out var compactAfter) && compactAfter.TryGetInt32(out var after) ? after : DepotNoteJoinResult.DefaultCompactAfter);
    }

    public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("Update", documentId, update, cancellationToken);

    public Task EditedAsync(string documentId, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("Edited", documentId, cancellationToken);

    public Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("Compact", documentId, snapshot, cancellationToken);

    public Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<bool>("Refill", documentId, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
