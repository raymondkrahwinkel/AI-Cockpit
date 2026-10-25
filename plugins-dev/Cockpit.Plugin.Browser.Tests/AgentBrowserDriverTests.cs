using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Browser.Tests;

public class AgentBrowserDriverTests
{
    private static readonly HttpClient Http = new();

    // AC-1713 acceptance 2 (L2b, one driver per surface): after Neem over a click of Raymond reaches the page and the
    // session's own click through the proxy does not, until Geef terug. The page counts clicks in its title.
    [Fact]
    public async Task TakeOver_BlocksTheSessionsClicks_AndGiveBackFreesThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "cockpit-browser-test-" + Guid.NewGuid().ToString("N"));
        var executable = new AgentBrowserRuntime(Path.Combine(Path.GetTempPath(), "cockpit-browser-test-none"), Http).CachedExecutable()
            ?? AgentBrowserRuntime.SystemBrowser()
            ?? throw new InvalidOperationException("No Chromium to test against: install Chrome or Edge.");
        using var browsers = new AgentBrowserHost(() => executable, root);
        var frames = 0;
        using var tabs = new AgentBrowserTabs(browsers, paneId => paneId, (name, _) =>
        {
            if (name == "agents.frame")
            {
                Interlocked.Increment(ref frames);
            }
        });
        try
        {
            var endpoint = await browsers.StartAsync("pane-a") ?? throw new InvalidOperationException("The agent browser did not start");
            var page = "data:text/html," + Uri.EscapeDataString("<title>0</title><button style='position:fixed;inset:0' onclick='document.title=+document.title+1'>x</button>");
            using (var opened = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/json/new?{page}")))
            {
                opened.EnsureSuccessStatusCode();
            }

            var (targetId, socketUrl) = await PageAsync(endpoint);
            using var agent = new ClientWebSocket();
            await agent.ConnectAsync(new Uri(socketUrl), CancellationToken.None);

            Assert.Null(await ClickAsync(agent, 1));
            await TitleBecomesAsync(endpoint, "1");

            browsers.SetOperatorDrives("pane-a", true);
            Assert.Equal(AgentCdpProxy.TakenOverMessage, await ClickAsync(agent, 3));
            Assert.True(await tabs.WatchAsync("pane-a", targetId, CancellationToken.None));
            await InputClickAsync(tabs, targetId);
            await TitleBecomesAsync(endpoint, "2");
            Assert.True(Volatile.Read(ref frames) > 0, "no screencast frame while the tab was on screen");

            // AC-1713 acceptance 1: with nobody looking there is no screencast, so the page changing sends nothing.
            tabs.Unwatch();
            await Task.Delay(500);
            var framesWhenClosed = Volatile.Read(ref frames);
            browsers.SetOperatorDrives("pane-a", false);
            Assert.Null(await ClickAsync(agent, 5));
            await TitleBecomesAsync(endpoint, "3");
            await Task.Delay(500);
            Assert.Equal(framesWhenClosed, Volatile.Read(ref frames));
        }
        finally
        {
            browsers.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task InputClickAsync(AgentBrowserTabs tabs, string targetId)
    {
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
        {
            var parameters = JsonSerializer.SerializeToElement(new { type, x = 100, y = 100, button = "left", clickCount = 1 });
            Assert.Null(await tabs.InputAsync("pane-a", targetId, "Input.dispatchMouseEvent", parameters));
        }
    }

    private static async Task<(string Id, string SocketUrl)> PageAsync(string endpoint)
    {
        using var list = JsonDocument.Parse(await Http.GetStringAsync($"{endpoint}/json/list"));
        var page = list.RootElement.EnumerateArray().First(item => item.GetProperty("type").GetString() == "page" && item.GetProperty("url").GetString()!.StartsWith("data:", StringComparison.Ordinal));
        return (page.GetProperty("id").GetString()!, page.GetProperty("webSocketDebuggerUrl").GetString()!);
    }

    // The session's click: a press and a release with their own ids. Returns the error message of the first answer
    // that is one, or null when both went through.
    private static async Task<string?> ClickAsync(ClientWebSocket socket, int firstId)
    {
        string? error = null;
        foreach (var (id, type) in new[] { (firstId, "mousePressed"), (firstId + 1, "mouseReleased") })
        {
            var command = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Input.dispatchMouseEvent", @params = new { type, x = 100, y = 100, button = "left", clickCount = 1 } });
            await socket.SendAsync(command, WebSocketMessageType.Text, true, CancellationToken.None);
            using var reply = JsonDocument.Parse(await ReceiveAnswerAsync(socket, id));
            if (reply.RootElement.TryGetProperty("error", out var failure))
            {
                error ??= failure.GetProperty("message").GetString();
            }
        }

        return error;
    }

    private static async Task<string> ReceiveAnswerAsync(ClientWebSocket socket, int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var text = new StringBuilder();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, timeout.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            }
            while (received.EndOfMessage == false);

            using var message = JsonDocument.Parse(text.ToString());
            if (message.RootElement.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
            {
                return text.ToString();
            }
        }
    }

    private static async Task TitleBecomesAsync(string endpoint, string expected)
    {
        var seen = string.Empty;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var list = JsonDocument.Parse(await Http.GetStringAsync($"{endpoint}/json/list"));
            seen = list.RootElement.EnumerateArray().First(item => item.GetProperty("url").GetString()!.StartsWith("data:", StringComparison.Ordinal)).GetProperty("title").GetString()!;
            if (seen == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(expected, seen);
    }
}
