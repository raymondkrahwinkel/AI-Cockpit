using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Inbox;

// AC-1586: one connection's Depot inbox — REST for the list and the counts, the hub for the push that says they changed.
// `handler` is the host's authorized handler for the depot.inbox scope; the bearer never reaches this class.
internal sealed class DepotInboxService : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly DepotConnectionRegistration _connection;
    private readonly HttpClient _http;
    private readonly DepotInboxHubFactory _hubFactory;
    private readonly Action<DepotInboxPage> _publish;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private IDepotInboxHub? _hub;
    private bool _signedInBefore;
    private int _attempt;

    public DepotInboxService(DepotConnectionRegistration connection, HttpMessageHandler handler, DepotInboxHubFactory hubFactory, Action<DepotInboxPage> publish, ILogger? logger = null)
    {
        _logger = logger;
        _connection = connection;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _hubFactory = hubFactory;
        _publish = publish;
        Page = _Empty(DepotInboxState.SignIn);
    }

    public DepotConnectionRegistration Connection => _connection;

    public DepotInboxPage Page { get; private set; }

    public async Task<DepotInboxPage> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Page = await _FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _publish(Page);
        return Page;
    }

    public async Task<DepotInboxPage> MarkReadAsync(string messageId, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(messageId, out var id))
        {
            try
            {
                using var response = await _http.PostAsync($"{_Base}/cockpit/inbox/{id:D}/read", null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Depot did not answer; the fetch below says so.
            }
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        await _CloseHubAsync().ConfigureAwait(false);
        _http.Dispose();
        _life.Dispose();
    }

    private string _Base => _connection.Url.TrimEnd('/');

    private async Task<DepotInboxPage> _FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var unreadResponse = await _http.GetAsync($"{_Base}/cockpit/inbox/unread", cancellationToken).ConfigureAwait(false);
            if (_Refused(unreadResponse))
            {
                return await _SignedOutAsync().ConfigureAwait(false);
            }

            unreadResponse.EnsureSuccessStatusCode();
            var unread = await unreadResponse.Content.ReadFromJsonAsync<_Unread>(DepotChannel.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException("Depot sent no counts.");
            using var listResponse = await _http.GetAsync($"{_Base}/cockpit/inbox?limit=50", cancellationToken).ConfigureAwait(false);
            if (_Refused(listResponse))
            {
                return await _SignedOutAsync().ConfigureAwait(false);
            }

            listResponse.EnsureSuccessStatusCode();
            var list = await listResponse.Content.ReadFromJsonAsync<_List>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);

            // The page is what Depot said just now and nothing else: a title it no longer gives is not filled in from before.
            var messages = (list?.Messages ?? []).Select(_Message).OfType<DepotInboxMessage>().ToList();
            var count = new DepotInboxCount(_connection.Id, DepotInboxState.Ok, unread.Unread, unread.PerOrganization ?? new Dictionary<string, int>());
            _signedInBefore = true;
            if (!await _EnsureHubAsync(unread.UserId, cancellationToken).ConfigureAwait(false))
            {
                return new DepotInboxPage(count with { State = DepotInboxState.Unreachable }, messages, DateTimeOffset.UtcNow, unread.UserId);
            }

            _attempt = 0;
            return new DepotInboxPage(count, messages, DateTimeOffset.UtcNow, unread.UserId);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The host answers a missing sign-in itself, so a failure on the wire means Depot, not the sign-in.
            _RetryLater();
            return Page.Count.State == DepotInboxState.Expired
                ? Page
                : Page with { Count = Page.Count with { State = DepotInboxState.Unreachable } };
        }
    }

    private static bool _Refused(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    // Nothing of the old list survives a refused sign-in, and the hub closes with it (acceptance 4).
    private async Task<DepotInboxPage> _SignedOutAsync()
    {
        await _CloseHubAsync().ConfigureAwait(false);
        return _Empty(_signedInBefore ? DepotInboxState.Expired : DepotInboxState.SignIn);
    }

    private async Task<bool> _EnsureHubAsync(string userId, CancellationToken cancellationToken)
    {
        if (_hub is not null)
        {
            return true;
        }

        var hub = _hubFactory(_connection, userId, _OnPush, _OnClosedAsync);
        try
        {
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
            _hub = hub;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
            _RetryLater();
            return false;
        }
    }

    // The counts change on the push itself; the list follows from REST, the one place that decides what is shown.
    private void _OnPush(DepotInboxPush push)
    {
        Page = Page with { Count = Page.Count with { Unread = push.Unread, PerOrganization = push.PerOrganization } };
        _publish(Page);
        // Push callbacks cannot await the REST refresh; the task logs failures and a later push retries.
        _ = _RefreshQuietlyAsync();
    }

    private Task _OnClosedAsync(Exception? exception)
    {
        _hub = null;
        _RetryLater();
        return Task.CompletedTask;
    }

    private async Task _CloseHubAsync()
    {
        var hub = Interlocked.Exchange(ref _hub, null);
        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ponytail: one retry per failure with a capped backoff; a burst of failures can queue a few, each a single fetch.
    private void _RetryLater()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        var delay = Backoff[Math.Min(_attempt++, Backoff.Length - 1)];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Page.Count.State is not (DepotInboxState.SignIn or DepotInboxState.Expired))
            {
                await _RefreshQuietlyAsync().ConfigureAwait(false);
            }
        });
    }

    private async Task _RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync(_life.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Disposed while it ran: there is nobody left to tell.
        }
        catch (Exception exception)
        {
            // Nobody awaits this one, so the failure is logged here and the next push or retry tries again.
            _logger?.LogWarning(exception, "Refreshing the Depot inbox of {Connection} failed.", _connection.Name);
        }
    }

    private DepotInboxPage _Empty(string state) =>
        new(new DepotInboxCount(_connection.Id, state, 0, new Dictionary<string, int>()), [], null);

    private DepotInboxMessage? _Message(_Listed listed) =>
        _Absolute(listed.OpenUrl) is not { } openUrl ? null : new DepotInboxMessage(
            listed.Id, listed.Kind, listed.IsRead, listed.Handled, listed.CreatedAt, listed.Organization, listed.Summary ?? string.Empty, openUrl,
            listed.Note is null ? null : listed.Note with { OpenUrl = _Absolute(listed.Note.OpenUrl) }, listed.Inviter);

    // Depot's links are root-relative; anything else (another host, a scheme) is not opened from here.
    private string? _Absolute(string? relative) =>
        relative is { Length: > 1 } && relative[0] == '/' && relative[1] != '/' && relative[1] != '\\' ? _Base + relative : null;

    private sealed record _Unread(string UserId, int Unread, Dictionary<string, int>? PerOrganization);

    private sealed record _List(IReadOnlyList<_Listed>? Messages);

    private sealed record _Listed(
        string Id, string Kind, bool IsRead, bool Handled, DateTimeOffset CreatedAt, DepotInboxOrganization? Organization,
        string? Summary, string? OpenUrl, DepotInboxNote? Note, string? Inviter = null);
}
