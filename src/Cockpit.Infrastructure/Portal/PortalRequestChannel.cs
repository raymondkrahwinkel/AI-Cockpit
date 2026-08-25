using Tmds.DBus;

namespace Cockpit.Infrastructure.Portal;

// The two-step shape every XDG desktop portal call has: the method only hands back a Request object
// path, and the actual result arrives on that Request's `Response` signal. Shared by the callers that
// speak to a portal (push-to-talk #34, screenshots AC-220) so the derivation exists once.
internal sealed class PortalRequestChannel(Connection connection, string requestSender)
{
    private const string BusName = "org.freedesktop.portal.Desktop";

    private int _requestCounter;

    // Connects the session bus and derives the request-path prefix from the unique name the bus hands out.
    public static async Task<PortalRequestChannel> ConnectAsync(Connection connection)
    {
        var info = await connection.ConnectAsync().ConfigureAwait(false);
        return new PortalRequestChannel(connection, DeriveRequestSender(info.LocalName));
    }

    // Portal request paths are namespaced under the caller's own unique bus name, leading ':' stripped and
    // every '.' turned into '_' — the portal spec's rule. Split out as the one part a test can reach: get it
    // wrong and the response signal arrives on a path nothing is listening to.
    internal static string DeriveRequestSender(string localName) => localName.TrimStart(':').Replace('.', '_');

    // A handle token unique within this channel, used to predict the request path before the call is made.
    public string NextToken(string prefix) => $"{prefix}{Interlocked.Increment(ref _requestCounter)}";

    // Invokes a portal method and waits for the matching Request's `Response` signal; the subscription is
    // made before the method is invoked, so the response can never race it. Cancelling stops waiting but
    // does not close the portal's own dialog — that belongs to the desktop, so a screenshot picker stays open.
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
