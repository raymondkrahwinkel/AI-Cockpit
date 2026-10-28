using System.Net;
using System.Net.Http.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The live notes that are open, one DepotNoteSession each, keyed by connection and note. The host-only token stays behind
// handlerFor: this class, the sessions and the channel only ever see the handler.
internal sealed class DepotNoteLive(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    DepotNotesHubFactory hubFactory,
    DepotNoteRoomFactory roomFactory,
    Action<DepotNoteLiveStatus> publish,
    ILogger? logger = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DepotNoteSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Open(DepotNoteLiveRequest request)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == request.ConnectionId)
            ?? throw new ArgumentException("That Depot connection is no longer configured.", nameof(request));
        DepotNoteSession session;
        lock (_lock)
        {
            var key = _Key(request);
            if (_sessions.TryGetValue(key, out var open))
            {
                session = open;
            }
            else
            {
                session = new DepotNoteSession(
                    connection, request, cancellationToken => ReadUserAsync(connection, handlerFor(connection), cancellationToken),
                    hubFactory, roomFactory, publish, logger: logger);
                _sessions[key] = session;
            }
        }

        // A session that stopped on SignInNeeded starts again here, once the user has signed in; one that is running ignores it.
        session.Connect();
    }

    public async Task CloseAsync(DepotNoteLiveRequest request)
    {
        DepotNoteSession? session;
        lock (_lock)
        {
            _sessions.Remove(_Key(request), out session);
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<DepotNoteSession> sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The subject the hub's gate compares expectedUser with (AC-1607); a 401 here is a sign-in the user has to redo.
    internal static async Task<string> ReadUserAsync(DepotConnectionRegistration connection, HttpMessageHandler handler, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(me?.UserId)
            ? throw new HttpRequestException("Depot did not identify the user.", null, HttpStatusCode.Unauthorized)
            : me.UserId;
    }

    private static string _Key(DepotNoteLiveRequest request) => $"{request.ConnectionId}|{request.Organization}/{request.Project}/{request.Path}";

    private sealed record Me(string? UserId);
}
