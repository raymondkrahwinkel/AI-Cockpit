using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes;

// One backend poller for the rail and every Notes view, including while the dock is closed.
internal sealed class DepotNotes(
    ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Action<DepotNotesPage> publish,
    ILogger? logger = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _inboxStates = new(StringComparer.Ordinal);
    private readonly Lock _statesLock = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private Task? _polling;

    public void Start() => _polling ??= _PollAsync();

    public async Task<DepotNotesPage> ListAsync(DepotNotesRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = connections().FirstOrDefault(candidate => candidate.Id == request.ConnectionId);
            var page = connection is null
                ? new DepotNotesPage(request.ConnectionId, DepotInboxState.SignIn, [])
                : await _FetchAsync(connection, cancellationToken).ConfigureAwait(false);
            _known.Add(request.ConnectionId);
            publish(page);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The inbox reconnects independently of the Notes poll. Its first healthy page requests an immediate resync.
    public void OnInboxPage(DepotInboxPage page)
    {
        bool reconnected;
        lock (_statesLock)
        {
            reconnected = page.Count.State == DepotInboxState.Ok
                && _inboxStates.GetValueOrDefault(page.Count.ConnectionId) != DepotInboxState.Ok;
            _inboxStates[page.Count.ConnectionId] = page.Count.State;
        }

        if (reconnected)
        {
            Refresh();
        }
    }

    public void Refresh() => _ = _RefreshQuietlyAsync();

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        if (_polling is { } polling)
        {
            await polling.ConfigureAwait(false);
        }
    }

    private async Task _PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                await _RefreshQuietlyAsync().ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // Plugin shutdown cancels both the timer and the current fetch.
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Polling Depot live notes failed.");
        }
    }

    private async Task _RefreshQuietlyAsync()
    {
        try
        {
            var current = connections();
            foreach (var connection in current)
            {
                await ListAsync(new(connection.Id), _life.Token).ConfigureAwait(false);
            }

            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                foreach (var id in _known.Where(id => !current.Any(connection => connection.Id == id)).ToList())
                {
                    _known.Remove(id);
                    publish(new(id, DepotInboxState.SignIn, []));
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // No UI survives this plugin's lifetime.
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Refreshing Depot live notes failed.");
        }
    }

    private async Task<DepotNotesPage> _FetchAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(host.CreateAuthorizedHandler(connection.McpServerName, "depot.host")) { Timeout = TimeSpan.FromSeconds(20) };
            using var api = new DepotApi(host, connection, http);
            var answer = await api.GetAsync("/cockpit/notes/live", cancellationToken).ConfigureAwait(false);
            if (answer.Outcome is DepotApiOutcome.NotSignedIn or DepotApiOutcome.Forbidden)
            {
                return new(connection.Id, DepotInboxState.SignIn, []);
            }

            if (answer.Outcome != DepotApiOutcome.Success)
            {
                return new(connection.Id, DepotInboxState.Unreachable, []);
            }

            var list = JsonSerializer.Deserialize<_List>(answer.Content.Span, DepotChannel.Json);
            if (list?.Notes is null || list.Notes.Any(note => note.People.ValueKind != JsonValueKind.Array))
            {
                throw new JsonException("Depot returned no live notes list or participants.");
            }
            return new(connection.Id, DepotInboxState.Ok, list.Notes.Select(note => new DepotLiveNote(
                note.Address, note.Project, note.Path, note.Title, note.Href, note.Join,
                note.People.EnumerateArray().Select(_Person).ToList())).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger?.LogWarning(exception, "Reading live notes from {Connection} failed.", connection.Name);
            return new(connection.Id, DepotInboxState.Unreachable, []);
        }
    }

    // DEP-535 adds name/color objects; existing Depots still send a string per participant.
    private static DepotNotePerson _Person(JsonElement person) => person.ValueKind == JsonValueKind.String
        ? new(person.GetString() ?? string.Empty)
        : person.Deserialize<DepotNotePerson>(DepotChannel.Json) is { Name: not null } named
            ? named : throw new JsonException("Depot returned no participant name.");

    private sealed record _List(IReadOnlyList<_Note> Notes);
    private sealed record _Note(string Address, string Project, string Path, string Title, string Href, bool Join, JsonElement People);
}
