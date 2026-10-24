using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.OAuth;

namespace Cockpit.Infrastructure.Plugins;

// AC-1625: a plugin's OAuth connect on this server, its browser step on the desktop whose channel call asked for it.
// The verifier and the token stay here. The desktop names its loopback port, gets the link, and hands back the query.
internal sealed class PluginOAuthSignIns : ISingletonService
{
    // How long a connect waits for its desktop to name a port; that desktop polls while its channel call runs.
    internal static readonly TimeSpan PortWait = TimeSpan.FromSeconds(30);

    private static readonly AsyncLocal<PluginOAuthRelay?> CurrentRelay = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginOAuthSignIn> _byFlow = new(StringComparer.Ordinal);

    // Set by the channel route around a desktop's call, so a connect inside it relays instead of opening a browser here.
    public static PluginOAuthRelay? Relay
    {
        get => CurrentRelay.Value;
        set => CurrentRelay.Value = value;
    }

    // The one waiting for the desktop that sent `relayId`, so no other desktop's poll ever picks it up.
    public PluginOAuthSignIn? Pending(string folderId, string relayId)
    {
        lock (_gate)
        {
            return _byFlow.Values.FirstOrDefault(signIn => signIn.FolderId == folderId && signIn.RelayId == relayId && signIn.AwaitsDesktop);
        }
    }

    public PluginOAuthSignIn? Find(string folderId, string name, string flowId)
    {
        lock (_gate)
        {
            return _byFlow.TryGetValue(flowId, out var signIn) && signIn.FolderId == folderId && signIn.Name == name ? signIn : null;
        }
    }

    // The browser step of one connect: a port from the desktop, the link out, the redirect's query back (null: none came).
    public async Task<(PluginOAuthOutcome Outcome, Uri? RedirectUri, string? Query)> RunAsync(
        PluginOAuthRelay relay,
        string providerId,
        string state,
        Func<Uri, Uri> linkFor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var signIn = new PluginOAuthSignIn(relay.FolderId, relay.RelayId, providerId, state, DateTimeOffset.UtcNow + timeout);
        lock (_gate)
        {
            _byFlow[signIn.FlowId] = signIn;
        }

        try
        {
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            expiry.CancelAfter(timeout);
            using var registration = expiry.Token.Register(signIn.End);
            if (await signIn.PortAsync(PortWait).ConfigureAwait(false) is not { } port)
            {
                return (PluginOAuthOutcome.Unavailable, null, null);
            }

            var redirectUri = McpOAuthLoopback.RedirectUriAt(port);
            signIn.Publish(linkFor(redirectUri));
            return (PluginOAuthOutcome.Ok, redirectUri, await signIn.RedirectAsync().ConfigureAwait(false));
        }
        finally
        {
            signIn.End();
            lock (_gate)
            {
                _byFlow.Remove(signIn.FlowId);
            }
        }
    }
}

// Which desktop call a connect runs inside, and what records how the sign-in ended (name and outcome, never a code).
// Closed when that call returns, so work it left running cannot relay to a desktop that stopped listening.
internal sealed record PluginOAuthRelay(string FolderId, string RelayId, Func<string, string, Task> Ended)
{
    private volatile bool _open = true;

    public bool Open => _open;

    public void Close() => _open = false;
}

internal sealed class PluginOAuthSignIn(string folderId, string relayId, string name, string state, DateTimeOffset expiresAt)
{
    private readonly TaskCompletionSource<int?> _port = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> _redirect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Uri? _url;

    public string FlowId { get; } = Guid.NewGuid().ToString("N");

    public string FolderId => folderId;

    public string RelayId => relayId;

    public string Name => name;

    public DateTimeOffset ExpiresAt => expiresAt;

    public bool Ended => _redirect.Task.IsCompleted;

    public bool AwaitsDesktop => !_port.Task.IsCompleted;

    // awaiting_desktop until a port is named, running while the link is out, ended once a redirect came or it gave up.
    public string Status => Ended ? "ended" : _url is null ? "awaiting_desktop" : "running";

    public Uri? Url => _url;

    public bool AwaitsRedirect => _url is not null && !Ended;

    // False when a port was named already or the sign-in ended: one desktop, one listener, one link.
    public bool Claim(int port) => !Ended && _port.TrySetResult(port);

    // False unless the link is out and the redirect carries its state: a code from any other request is turned away
    // without ending this one. An error redirect with the state goes through, and the connect answers Cancelled.
    public bool Submit(string query)
    {
        var returned = McpOAuthLoopback.ParseCallback(query).State;
        return AwaitsRedirect
            && returned is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returned))
            && _redirect.TrySetResult(query);
    }

    // Ends it without a redirect; the connect answers Unavailable or Cancelled and any earlier token stays.
    public void End()
    {
        _port.TrySetResult(null);
        _redirect.TrySetResult(null);
        _link.TrySetResult();
    }

    internal Task LinkAsync(TimeSpan wait, CancellationToken cancellationToken) => Task.WhenAny(_link.Task, Task.Delay(wait, cancellationToken));

    internal async Task<int?> PortAsync(TimeSpan wait)
    {
        try
        {
            return await _port.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    internal void Publish(Uri link)
    {
        _url = link;
        _link.TrySetResult();
    }

    internal Task<string?> RedirectAsync() => _redirect.Task;
}
