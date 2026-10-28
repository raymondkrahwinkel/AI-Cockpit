// AC-1896 spike: the .NET side of /hubs/notes (protocol §1-§4), cookie-signed like a browser tab. Handlers are registered before Join
// and updates that arrive early are kept, as the protocol asks (§3 step 1).
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

internal sealed record JoinResult(string DocumentId, string Role, bool Seed, bool AwaitingSeed, bool SchemaMismatch, string? Saver, string Markdown, List<byte[]> Rows);

internal sealed class NoteClient : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly List<byte[]> _early = [];
    private string? _documentId;
    public Action<byte[]>? OnUpdate { get; set; }
    public Action<string, byte[]>? OnPresence { get; set; }
    public readonly List<string> Refusals = [];

    public NoteClient(string baseUrl, CookieContainer cookies, string userId)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(baseUrl), $"/hubs/notes?expectedUser={userId}"), HttpTransportType.WebSockets, o =>
            {
                o.Cookies = cookies;
                o.Headers["Origin"] = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
            })
            .Build();
        _hub.On<string, byte[]>("update", (id, data) =>
        {
            lock (_early)
            {
                if (_documentId is null)
                {
                    _early.Add(data);
                    return;
                }
            }
            if (id == _documentId)
            {
                OnUpdate?.Invoke(data);
            }
        });
        _hub.On<string, string, byte[]>("presence", (id, from, data) =>
        {
            if (id == _documentId)
            {
                OnPresence?.Invoke(from, data);
            }
        });
        _hub.On<string>("refused", code => Refusals.Add(code));
    }

    public async Task<JoinResult> JoinAsync(string org, string project, string path, string schema, bool save)
    {
        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync();
        }
        var r = await _hub.InvokeAsync<JsonElement>("Join", new { org, project, path, schema, save });
        string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool B(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var rows = r.GetProperty("updates").EnumerateArray().Select(u => u.GetProperty("data").GetBytesFromBase64()).ToList();
        lock (_early)
        {
            _documentId = S("documentId");
            rows.AddRange(_early);
            _early.Clear();
        }
        return new JoinResult(_documentId!, S("role")!, B("seed"), B("awaitingSeed"), B("schemaMismatch"), S("saver"), S("markdown") ?? "", rows);
    }

    public string? ConnectionId => _hub.ConnectionId;
    public Task UpdateAsync(byte[] update) => _hub.InvokeAsync("Update", _documentId, update);
    public Task EditedAsync() => _hub.InvokeAsync("Edited", _documentId);
    public Task PresenceAsync(byte[] awareness) => _hub.InvokeAsync("Presence", _documentId, awareness);

    public async ValueTask DisposeAsync()
    {
        await _hub.StopAsync();
        await _hub.DisposeAsync();
    }
}
