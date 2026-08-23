using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

// AC-505: DiscoverProbeTimeout (5s default) also bounds the interactive OAuth sign-in on that request's 401,
// leaving too little time to grant consent — raised here alongside InitializationTimeout, since the shorter one
// governs. Shared by McpToolProvider and McpOAuthCoordinator so the pairing can't drift between them.
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
