using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// `OpencodeAcpConnection` against a `FakeCliSubprocess` (AC-783) — proves the JSON-RPC transport under the
// opencode ACP driver: a request gets its correlated reply, a JSON-RPC error surfaces as an exception with
// its message built from only code+message (never the raw error object, which could echo request data such
// as a Bearer token back into a UI status line), notifications and agent-initiated requests are routed to
// their own streams, and a request outstanding when the stream ends fails rather than hangs. Mirrors
// Cockpit.Plugin.KimiProvider.Tests.KimiAcpConnectionTests — this transport is an unmodified copy of Kimi's
// own, so only the core correctness properties are re-asserted here, not the full backpressure/stderr-deadlock
// stress suite Kimi's own tests already cover for the identical channel-based implementation.
public class OpencodeAcpConnectionTests
{
    private static readonly Dictionary<string, string?> _NoEnv = new();

    [Fact]
    public async Task SendRequest_CorrelatesTheReplyById_AndReturnsItsResult()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new OpencodeAcpConnection(fake);
        connection.Start("opencode", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("initialize", new { protocolVersion = 1 });
        var id = await _WaitForRequestIdAsync(fake, "initialize");
        await fake.PushStdoutAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"protocolVersion":1}}""");

        var result = await requestTask;

        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task SendRequest_Throws_WhenTheReplyIsAJsonRpcError()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new OpencodeAcpConnection(fake);
        connection.Start("opencode", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() });
        var id = await _WaitForRequestIdAsync(fake, "session/new");
        await fake.PushStdoutAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32603,"message":"Internal error: OpenCode service failure"}}""");

        await Assert.ThrowsAsync<OpencodeAcpException>(() => requestTask);
    }

    // The exception message must be built from only error.code + error.message — never the raw error object —
    // because it propagates straight into a UI status line; an echoed error.data must never end up there.
    [Fact]
    public async Task SendRequest_WhenTheReplyIsAJsonRpcError_ExceptionMessageOmitsErrorData()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new OpencodeAcpConnection(fake);
        connection.Start("opencode", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() });
        var id = await _WaitForRequestIdAsync(fake, "session/new");
        await fake.PushStdoutAsync($$$$$$"""{"jsonrpc":"2.0","id":{{{{{{id}}}}}},"error":{"code":-32000,"message":"unauthorized","data":{"params":{"headers":{"Authorization":"Bearer super-secret-token"}}}}}""");

        var exception = await Assert.ThrowsAsync<OpencodeAcpException>(() => requestTask);
        Assert.Equal("opencode acp error -32000: unauthorized", exception.Message);
        Assert.DoesNotContain("super-secret-token", exception.Message);
    }

    [Fact]
    public async Task Notifications_AndServerRequests_AreRoutedToTheirOwnStreams()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new OpencodeAcpConnection(fake);
        connection.Start("opencode", Path.GetTempPath(), _NoEnv);

        // Measured live: opencode's own server-request ids start at 0, unlike a client's own (which start at 1)
        // — this connection must not assume any particular starting id.
        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session-1","update":{"sessionUpdate":"agent_message_chunk"}}}""");
        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"session-1","toolCall":{"toolCallId":"item-1"}}}""");

        var notification = await _FirstNotificationAsync(connection);
        var serverRequest = await _FirstServerRequestAsync(connection);

        Assert.Equal("session/update", notification.Method);
        Assert.Equal("session/request_permission", serverRequest.Method);
        Assert.Equal(0, serverRequest.Id.GetInt32());
        Assert.Equal("session-1", serverRequest.Params.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SendRequest_Fails_WhenTheStreamEndsBeforeAReply()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new OpencodeAcpConnection(fake);
        connection.Start("opencode", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("initialize", new { protocolVersion = 1 });
        await _WaitForRequestIdAsync(fake, "initialize");
        fake.CompleteStdout();

        await Assert.ThrowsAsync<OpencodeAcpException>(() => requestTask);
    }

    private static async Task<long> _WaitForRequestIdAsync(FakeCliSubprocess fake, string method)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written => written.Contains($"\"method\":\"{method}\""));
            if (line is not null)
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("id").GetInt64();
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No request for method '{method}' was written.");
    }

    private static async Task<OpencodeNotification> _FirstNotificationAsync(OpencodeAcpConnection connection)
    {
        await foreach (var notification in connection.Notifications)
        {
            return notification;
        }

        throw new InvalidOperationException("No notification was produced.");
    }

    private static async Task<OpencodeServerRequest> _FirstServerRequestAsync(OpencodeAcpConnection connection)
    {
        await foreach (var request in connection.ServerRequests)
        {
            return request;
        }

        throw new InvalidOperationException("No server request was produced.");
    }
}
