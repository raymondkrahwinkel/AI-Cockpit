using System.Text.Json;
using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

// Connects to one MCP server, carrying the compatibility retry AC-928 needs. ModelContextProtocol 2.0.0 defaults to
// the 2026-07-28 revision (SEP-2575), which opens with a `server/discover` probe and falls back to the `initialize`
// handshake when that probe fails. YouTrack's MCP server (measured against 2026.2) answers every unknown JSON-RPC
// method with HTTP 200 and an empty `result` instead of -32601 Method not found, so the probe looks like a success
// and then dies deserializing DiscoverResult — a failure the SDK's own fallback does not cover, identically in SDK
// 2.0.0 and 2.1.0. Retrying once with the version pinned to the last initialize-capable revision connects that
// server, and leaves every conforming server on the newest revision, which a blanket pin would not: from 2026-07-28
// on there is no `initialize` handshake at all, so a discover-only server would become unreachable instead.
internal static class McpClientConnector
{
    // The newest revision that still speaks the `initialize` handshake, which is what the retry pins to.
    private const string InitializeHandshakeVersion = "2025-11-25";

    /// <summary>
    /// Connects to <paramref name="transport"/>, retrying once on a server whose discover response cannot be read.
    /// The retry pins <paramref name="options"/>'s protocol version, so pass an instance this call may keep.
    /// </summary>
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
