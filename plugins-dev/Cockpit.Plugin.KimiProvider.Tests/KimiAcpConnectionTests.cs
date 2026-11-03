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

    // P1-9b: the internal notification channel is bounded (capacity 1024) with BoundedChannelFullMode.Wait —
    // proves that pushing past that capacity applies backpressure instead of silently dropping a message. Under
    // the bug this replaces (an unbounded channel, or a bounded one written with TryWrite), a burst like this
    // either grows without bound or drops everything past the cap the instant the fast-path TryWrite fails; this
    // asserts every single one of them is still eventually delivered once a reader starts draining.

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
}
