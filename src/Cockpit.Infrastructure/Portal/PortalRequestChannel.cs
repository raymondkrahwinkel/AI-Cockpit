using Tmds.DBus;

namespace Cockpit.Infrastructure.Portal;

/// <summary>
/// The two-step shape every XDG desktop portal call has: the method itself only hands back a Request object
/// path, and the actual result arrives on that Request's <c>Response</c> signal. Shared by the callers that
/// speak to a portal — global shortcuts (push-to-talk, #34) and screenshots (AC-220) — so the request-path
/// derivation exists once rather than being copied per portal interface.
/// </summary>
internal sealed class PortalRequestChannel(Connection connection, string requestSender)
{
    private const string BusName = "org.freedesktop.portal.Desktop";

    private int _requestCounter;

    /// <summary>Connects the session bus and derives the request-path prefix from the unique name the bus hands out.</summary>
    public static async Task<PortalRequestChannel> ConnectAsync(Connection connection)
    {
        var info = await connection.ConnectAsync().ConfigureAwait(false);
        return new PortalRequestChannel(connection, DeriveRequestSender(info.LocalName));
    }

    /// <summary>
    /// Portal request object paths are namespaced under the caller's own unique bus name, with the leading
    /// ':' stripped and every '.' turned into '_' — the portal spec's rule. Split out because it is the one
    /// part of this class a test can reach: get it wrong and the response signal arrives on a path nothing is
    /// listening to, which looks exactly like a portal that never answered.
    /// </summary>
    internal static string DeriveRequestSender(string localName) => localName.TrimStart(':').Replace('.', '_');

    /// <summary>A handle token unique within this channel, used to predict the request path before the call is made.</summary>
    public string NextToken(string prefix) => $"{prefix}{Interlocked.Increment(ref _requestCounter)}";

    /// <summary>
    /// Invokes a portal method and waits for the matching Request's <c>Response</c> signal. The subscription is
    /// made before the method is invoked, so the response can never race it.
    /// </summary>
    /// <param name="invoke">Calls the portal method, passing it the handle token it must be given.</param>
    /// <remarks>
    /// Cancelling stops waiting; it does not close the portal's own dialog, which belongs to the desktop and
    /// not to us. That matters for the interactive calls (a screenshot picker sits open for as long as the
    /// operator wants) — the wait is abandoned, the picker is theirs to dismiss.
    /// </remarks>
    public async Task<PortalResponse> InvokeAsync(Func<string, Task<ObjectPath>> invoke, CancellationToken cancellationToken = default)
    {
        var token = NextToken("req");
        var requestPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{requestSender}/{token}");
        var request = connection.CreateProxy<IPortalRequest>(BusName, requestPath);

        var responseSource = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var responseWatch = await request
            .WatchResponseAsync(response => responseSource.TrySetResult(new PortalResponse(response.ResponseCode, response.Results)))
            .ConfigureAwait(false);

        await invoke(token).ConfigureAwait(false);
        return await responseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
