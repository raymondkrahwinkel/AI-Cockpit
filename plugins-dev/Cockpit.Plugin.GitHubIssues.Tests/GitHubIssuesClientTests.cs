using System.Net;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// The HTTP-mode path (AC-519), driven against a real loopback `HttpListener` rather than the real
// GitHub API — `GitHubIssuesClient.BaseUrl` exists solely so this can point the real client at it. This
// is the "test per pad" the ticket asks for on the HTTP side: the actual request URL and response parsing run, not
// a stand-in for them.
public class GitHubIssuesClientTests : IDisposable
{
    private readonly string _originalBaseUrl = GitHubIssuesClient.BaseUrl;

    public void Dispose() => GitHubIssuesClient.BaseUrl = _originalBaseUrl;

    [Fact]
    public async Task GetOpenIssuesAsync_WithALabel_SendsItAsAQueryParameter()
    {
        // Captured rather than asserted inside the server callback: an assertion failure there is an unobserved
        // exception on the listener's own loop, which answers with nothing at all — the client then sits out its
        // full HTTP timeout instead of failing fast on a clear message.
        string? capturedQuery = null;
        using var server = LoopbackServer.Start(request =>
        {
            capturedQuery = request.Url?.Query;
            return LoopbackServer.Json("""[]""");
        });
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, _) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None, label: "in progress");

        Assert.Empty(issues);
        Assert.Equal(1, server.RequestCount);
        Assert.Contains("labels=in%20progress", capturedQuery);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_ALabelContainingAComma_StillFindsTheMatchingIssue()
    {
        // Adversarial-review defect: GitHub's REST "labels" query parameter is a documented comma-separated list —
        // there is no quoting mechanism for a comma inside one label's own name (unlike the gh path's "label:"
        // search qualifier, which takes one quoted string — see GitHubGhClient.LabelSearchTerm). GitHub decodes the
        // escaped comma and splits the parameter on it, then requires an issue to carry every one of the resulting
        // names (AND semantics) — so a label literally named "ready, honestly" sent through &labels= is read back as
        // two filters, "ready" and "honestly", neither of which exists as its own label. This fake server plays that
        // real splitting rule, so the test reproduces the actual defect rather than a stand-in for it: an issue
        // whose one label contains a comma must be unreachable through that parameter no matter how the comma is
        // escaped, which is exactly what proves this is an API limitation and not a missing escape.
        const string body = """
            [
                { "number": 1, "title": "Has the comma label", "html_url": "https://x/1", "labels": [ { "name": "ready, honestly" } ] },
                { "number": 2, "title": "Unrelated", "html_url": "https://x/2", "labels": [ { "name": "bug" } ] }
            ]
            """;
        using var server = LoopbackServer.Start(request =>
        {
            var labelsParam = _QueryParam(request, "labels");
            if (labelsParam is null)
            {
                return LoopbackServer.Json(body);
            }

            // GitHub's own rule for this parameter: split on comma, AND the parts together.
            var wanted = labelsParam.Split(',', StringSplitOptions.TrimEntries);
            using var document = JsonDocument.Parse(body);
            var matching = document.RootElement.EnumerateArray()
                .Where(issue => wanted.All(name => issue.GetProperty("labels").EnumerateArray()
                    .Any(label => string.Equals(label.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))))
                .Select(issue => issue.GetRawText());
            return LoopbackServer.Json("[" + string.Join(",", matching) + "]");
        });
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, _) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None, label: "ready, honestly");

        Assert.Equal([1], issues.Select(issue => issue.Number));
    }

    private static string? _QueryParam(HttpListenerRequest request, string name)
    {
        var query = request.Url?.Query.TrimStart('?') ?? string.Empty;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    [Fact]
    public async Task GetOpenIssuesAsync_RequestsExactlyTheDocumentedPageLimit()
    {
        string? capturedQuery = null;
        using var server = LoopbackServer.Start(request =>
        {
            capturedQuery = request.Url?.Query;
            return LoopbackServer.Json("""[]""");
        });
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Contains($"per_page={GitHubIssuesClient.IssuePageLimit}", capturedQuery);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_FiltersOutPullRequests()
    {
        const string body = """
            [
                { "number": 1, "title": "Real issue", "html_url": "https://x/1" },
                { "number": 2, "title": "A pull request", "html_url": "https://x/2", "pull_request": { "url": "https://x/pr/2" } }
            ]
            """;
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(body));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, wasTruncated) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal([1], issues.Select(issue => issue.Number));
        Assert.False(wasTruncated);
    }

    [Fact]
    public async Task GetRepositoryLabelsAsync_ReadsNamesThroughTheSharedNormalization()
    {
        // Real REST labels shape: an array of objects with more than just "name" (color, description, id, ...).
        const string body = """
            [
                { "id": 1, "name": "bug", "color": "d73a4a", "description": "" },
                { "id": 2, "name": "in progress", "color": "ffffff", "description": null }
            ]
            """;
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(body));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var labels = await new GitHubIssuesClient().GetRepositoryLabelsAsync("octocat", "hello-world", token: null, CancellationToken.None);

        Assert.Equal(["bug", "in progress"], labels);
    }

    // A minimal loopback HTTP server for driving the real `GitHubIssuesClient` without the real GitHub API.
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<HttpListenerRequest, (HttpStatusCode Status, string ContentType, byte[] Body)> _respond;
        private int _requestCount;

        private LoopbackServer(HttpListener listener, Func<HttpListenerRequest, (HttpStatusCode, string, byte[])> respond)
        {
            _listener = listener;
            _respond = respond;
            _ = _ServeAsync();
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static LoopbackServer Start(Func<HttpListenerRequest, (HttpStatusCode Status, string ContentType, byte[] Body)> respond)
        {
            var listener = new HttpListener();
            var port = _FindFreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new LoopbackServer(listener, respond) { BaseUrl = $"http://127.0.0.1:{port}" };
        }

        public static (HttpStatusCode Status, string ContentType, byte[] Body) Json(string body) =>
            (HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(body));

        private async Task _ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);

                // A misbehaving handler must still answer with something: leaving the client's socket hanging turns
                // a test bug into a multi-minute HTTP-timeout wait instead of a fast, clear failure.
                var (status, contentType, body) = LoopbackServer._Respond(_respond, context.Request);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        private static (HttpStatusCode Status, string ContentType, byte[] Body) _Respond(
            Func<HttpListenerRequest, (HttpStatusCode Status, string ContentType, byte[] Body)> respond, HttpListenerRequest request)
        {
            try
            {
                return respond(request);
            }
            catch (Exception exception)
            {
                return (HttpStatusCode.InternalServerError, "text/plain", Encoding.UTF8.GetBytes(exception.ToString()));
            }
        }

        private static int _FindFreePort()
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        public void Dispose() => _listener.Close();
    }
}
