using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

// AC-505 follow-up (2026-07-29, live-verified against production Depot): ModelContextProtocol.Core 2.0.0 added
// McpClientOptions.DiscoverProbeTimeout (5s default), which bounds the initial protocol-version-discovery
// request — but a 401 on that same request is where the SDK also runs the interactive OAuth sign-in, so the
// default leaves an operator about five seconds to see a browser tab and grant consent before the whole
// connection attempt is cancelled and the authorization callback comes back null. Both timeouts have to move
// together: DiscoverProbeTimeout only takes effect while it is shorter than InitializationTimeout, so raising
// one without the other changes nothing. Shared by McpToolProvider and McpOAuthCoordinator, the two call sites
// that can end up running an interactive sign-in, so the pairing cannot drift between them on a future change.
internal static class McpInteractiveOAuthClientOptions
{
    // A fresh instance per connect, not one shared one: McpClientConnector's AC-928 retry pins the protocol version
    // on the options it is handed, and a shared instance would carry that pin into every later sign-in.
    public static McpClientOptions Create() => new()
    {
        InitializationTimeout = TimeSpan.FromMinutes(5),
        DiscoverProbeTimeout = TimeSpan.FromMinutes(5),
    };
}
