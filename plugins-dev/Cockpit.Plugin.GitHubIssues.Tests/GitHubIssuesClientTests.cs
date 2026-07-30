using System.Net;
using System.Text;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The HTTP-mode path (AC-519), driven against a real loopback <see cref="HttpListener"/> rather than the real
/// GitHub API — <see cref="GitHubIssuesClient.BaseUrl"/> exists solely so this can point the real client at it. This
/// is the "test per pad" the ticket asks for on the HTTP side: the actual request URL and response parsing run, not
/// a stand-in for them.
/// </summary>
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

        var issues = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None, label: "in progress");

        Assert.Empty(issues);
        Assert.Equal(1, server.RequestCount);
        Assert.Contains("labels=in%20progress", capturedQuery);
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
    public async Task GetOpenIssuesAsync_ExactlyAtThePageLimit_ReturnsAllOfThemUntruncated()
    {
        // The boundary AC-519's truncation warning keys on: real parsing of a real page of exactly the limit.
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(_IssuesJson(GitHubIssuesClient.IssuePageLimit)));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var issues = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal(GitHubIssuesClient.IssuePageLimit, issues.Count);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_OneMoreThanThePageLimit_TheServerIsWhatWouldCapIt()
    {
        // gh/GitHub itself does the capping, not this client — asking for 101 back (a server that ignores per_page)
        // proves the client does not impose a second, silent limit of its own on top of the real one.
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(_IssuesJson(GitHubIssuesClient.IssuePageLimit + 1)));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var issues = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal(GitHubIssuesClient.IssuePageLimit + 1, issues.Count);
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

        var issues = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal([1], issues.Select(issue => issue.Number));
    }

    [Fact]
    public async Task GetRepositoryLabelsAsync_ARepoWithNoLabels_ReturnsEmptyRatherThanThrowing()
    {
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json("""[]"""));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var labels = await new GitHubIssuesClient().GetRepositoryLabelsAsync("octocat", "hello-world", token: null, CancellationToken.None);

        Assert.Empty(labels);
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

    [Fact]
    public async Task GetRepositoryLabelsAsync_ARepositoryThatCannotBeReached_ThrowsRatherThanSilentlyEmptying()
    {
        // The dialog is the one that decides to fail open on this (a filter aid, not the issue list itself); the
        // client itself must still surface a real server error rather than swallow it.
        using var server = LoopbackServer.Start(_ => (HttpStatusCode.NotFound, "application/json", """{"message":"Not Found"}"""u8.ToArray()));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubIssuesClient().GetRepositoryLabelsAsync("octocat", "does-not-exist", token: null, CancellationToken.None));
    }

    private static string _IssuesJson(int count) =>
        "[" + string.Join(",", Enumerable.Range(1, count).Select(number => $$"""{ "number": {{number}}, "title": "Issue {{number}}", "html_url": "https://x/{{number}}" }""")) + "]";

    /// <summary>A minimal loopback HTTP server for driving the real <see cref="GitHubIssuesClient"/> without the real GitHub API.</summary>
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
