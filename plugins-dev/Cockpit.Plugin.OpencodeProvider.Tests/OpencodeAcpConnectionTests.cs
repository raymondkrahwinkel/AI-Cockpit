using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// AC-783: `OpencodeAcpConnection` against a `FakeCliSubprocess` — proves the JSON-RPC transport. Mirrors
// KimiAcpConnectionTests; this transport is an unmodified copy of Kimi's own, so only the core correctness
// properties are re-asserted here, not the full backpressure/stderr-deadlock stress suite.
public class OpencodeAcpConnectionTests
{
    private static readonly Dictionary<string, string?> _NoEnv = new();

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
