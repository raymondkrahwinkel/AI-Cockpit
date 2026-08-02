using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider.Tests;

// `KimiAcpConnection` against a `FakeCliSubprocess` (AC-268) — proves the JSON-RPC
// transport under the Kimi ACP driver: a request gets its correlated reply, a JSON-RPC error surfaces as an
// exception, notifications and agent-initiated requests are routed to their own streams, a request
// outstanding when the stream ends fails rather than hangs, and stderr is drained concurrently so a bounded
// pipe never blocks the child (D14).
public class KimiAcpConnectionTests
{
    private static readonly Dictionary<string, string?> _NoEnv = new();

    [Fact]
    public async Task SendRequest_CorrelatesTheReplyById_AndReturnsItsResult()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

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
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() });
        var id = await _WaitForRequestIdAsync(fake, "session/new");
        await fake.PushStdoutAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32000,"message":"authRequired"}}""");

        await Assert.ThrowsAsync<KimiAcpException>(() => requestTask);
    }

    // P1-8: the exception message must be built from only error.code + error.message — never the raw error
    // object — because it propagates straight into a UI status line (SessionViewModel's
    // Status = $"Failed to start: {ex.Message}"); an echoed error.data (kimi can echo the request's own params)
    // must never end up there, e.g. a Bearer token.
    [Fact]
    public async Task SendRequest_WhenTheReplyIsAJsonRpcError_ExceptionMessageOmitsErrorData()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() });
        var id = await _WaitForRequestIdAsync(fake, "session/new");
        await fake.PushStdoutAsync($$$$$$"""{"jsonrpc":"2.0","id":{{{{{{id}}}}}},"error":{"code":-32000,"message":"authRequired","data":{"params":{"headers":{"Authorization":"Bearer super-secret-token"}}}}}""");

        var exception = await Assert.ThrowsAsync<KimiAcpException>(() => requestTask);
        Assert.Equal("kimi acp error -32000: authRequired", exception.Message);
        Assert.DoesNotContain("super-secret-token", exception.Message);
    }

    [Fact]
    public async Task Notifications_AndServerRequests_AreRoutedToTheirOwnStreams()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session-1","update":{"sessionUpdate":"agent_message_chunk"}}}""");
        await fake.PushStdoutAsync("""{"jsonrpc":"2.0","id":7,"method":"session/request_permission","params":{"sessionId":"session-1","toolCall":{"toolCallId":"item-1"}}}""");

        var notification = await _FirstNotificationAsync(connection);
        var serverRequest = await _FirstServerRequestAsync(connection);

        Assert.Equal("session/update", notification.Method);
        Assert.Equal("session/request_permission", serverRequest.Method);
        Assert.Equal(7, serverRequest.Id.GetInt32());
        Assert.Equal("session-1", serverRequest.Params.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SendRequest_Fails_WhenTheStreamEndsBeforeAReply()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("initialize", new { protocolVersion = 1 });
        await _WaitForRequestIdAsync(fake, "initialize");
        fake.CompleteStdout();

        await Assert.ThrowsAsync<KimiAcpException>(() => requestTask);
    }

    [Fact]
    public async Task Start_DrainsStderrConcurrently_SoABoundedStderrPipeNeverBlocksTheConnection()
    {
        var fake = new FakeCliSubprocess(stderrCapacity: 1);
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        // Push more stderr lines than the bounded channel's capacity. Without a dedicated concurrent drain
        // task, the write past capacity would block forever — a full pipe deadlocking the child (D14).
        var pushStderr = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await fake.PushStderrAsync($"progress {i}");
            }
        });

        var finishedInTime = await Task.WhenAny(pushStderr, Task.Delay(TimeSpan.FromSeconds(3))) == pushStderr;

        Assert.True(finishedInTime, "a dedicated stderr-drain task must keep a bounded stderr pipe from blocking the connection");
    }

    // P1-9b: the internal notification channel is bounded (capacity 1024) with BoundedChannelFullMode.Wait —
    // proves that pushing past that capacity applies backpressure instead of silently dropping a message. Under
    // the bug this replaces (an unbounded channel, or a bounded one written with TryWrite), a burst like this
    // either grows without bound or drops everything past the cap the instant the fast-path TryWrite fails; this
    // asserts every single one of them is still eventually delivered once a reader starts draining.
    [Fact]
    public async Task Notifications_PushedFasterThanConsumed_PastBoundedCapacity_DropsNone()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new KimiAcpConnection(fake);
        connection.Start("kimi", Path.GetTempPath(), _NoEnv);

        const int total = 1100; // past the 1024 bounded capacity
        const string notificationJson = """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"tick"}}}}""";
        for (var i = 0; i < total; i++)
        {
            await fake.PushStdoutAsync(notificationJson);
        }

        // Give the background read loop a chance to run as far as it can before anything drains the channel.
        // Under the bug, everything past the cap would already be silently gone by now; under the fix, the
        // dispatch loop is instead blocked mid-write, holding every message it has not yet handed off.
        await Task.Delay(200);

        var received = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var _ in connection.Notifications.WithCancellation(timeout.Token))
            {
                received++;
                if (received == total)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Times out here under the bug, since "total" is never reached — caught so the assertion below
            // reports a clean, readable failure instead of an unhandled exception from the cancelled iteration.
        }

        Assert.Equal(total, received);
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

    private static async Task<KimiNotification> _FirstNotificationAsync(KimiAcpConnection connection)
    {
        await foreach (var notification in connection.Notifications)
        {
            return notification;
        }

        throw new InvalidOperationException("No notification was produced.");
    }

    private static async Task<KimiServerRequest> _FirstServerRequestAsync(KimiAcpConnection connection)
    {
        await foreach (var request in connection.ServerRequests)
        {
            return request;
        }

        throw new InvalidOperationException("No server request was produced.");
    }
}
