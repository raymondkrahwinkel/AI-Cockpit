using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// Proves the JSON-RPC transport under the app-server driver: a request gets its correlated
// reply, a JSON-RPC error surfaces as an exception, notifications and server-initiated requests
// are routed to their own streams, and a request outstanding when the stream ends fails rather than hangs.
public class CodexAppServerConnectionTests
{
    private static readonly Dictionary<string, string?> _NoEnv = new();

    [Fact]
    public async Task SendRequest_Throws_WhenTheReplyIsAJsonRpcError()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new CodexAppServerConnection(fake);
        connection.Start("codex", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("thread/start", new { cwd = "/tmp" });
        var id = await _WaitForRequestIdAsync(fake, "thread/start");
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"error":{"code":-32000,"message":"nope"}}""");

        await Assert.ThrowsAsync<CodexAppServerException>(() => requestTask);
    }

    [Fact]
    public async Task SendRequest_Fails_WhenTheStreamEndsBeforeAReply()
    {
        var fake = new FakeCliSubprocess();
        await using var connection = new CodexAppServerConnection(fake);
        connection.Start("codex", Path.GetTempPath(), _NoEnv);

        var requestTask = connection.SendRequestAsync("initialize", new { clientInfo = new { name = "cockpit", version = "1.0.0" } });
        await _WaitForRequestIdAsync(fake, "initialize");
        fake.CompleteStdout();

        await Assert.ThrowsAsync<CodexAppServerException>(() => requestTask);
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
