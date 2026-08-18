using System.Text.Json;
using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

// AC-928: YouTrack's MCP server answers an unknown method with an empty HTTP 200 instead of -32601, so the SDK's
// own fallback from the 2026-07-28 `server/discover` probe to `initialize` never triggers. Retry once pinned to the
// last initialize-capable revision, rather than pinning every server and losing the newer one.
internal static class McpClientConnector
{
    // The newest revision that still speaks the `initialize` handshake, which is what the retry pins to.
    private const string InitializeHandshakeVersion = "2025-11-25";

    // Connects to `transport`, retrying once on a server whose discover response cannot be read. The retry pins
    // the protocol version on `options`, so pass an instance this call may keep rather than a shared one.
    public static async Task<McpClient> ConnectAsync(
        IClientTransport transport,
        McpClientOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Only the discover probe can fail this way before a session exists, and the transport is reusable for a
            // second connect (verified against the live YouTrack endpoint), so this costs the broken server one extra
            // round trip. A retry that fails too throws, leaving the caller's own "skipping its tools" path intact.
            options ??= new McpClientOptions();
            options.ProtocolVersion = InitializeHandshakeVersion;
            return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
