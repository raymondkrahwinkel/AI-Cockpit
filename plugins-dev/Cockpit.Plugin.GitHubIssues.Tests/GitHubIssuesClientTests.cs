using System.Net;
using System.Text;
using System.Text.Json;

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

    [Fact]
    public async Task GetOpenIssuesAsync_ALabelContainingAComma_IsNeverSentThroughTheLabelsParameter()
    {
        // Half of the same fix: whatever the client does instead, it must not still hand a comma-containing name to
        // a parameter that is documented to split on comma — that would be sending the same broken request under a
        // different disguise.
        string? capturedQuery = null;
        using var server = LoopbackServer.Start(request =>
        {
            capturedQuery = request.Url?.Query;
            return LoopbackServer.Json("""[]""");
        });
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None, label: "ready, honestly");

        Assert.DoesNotContain("labels=", capturedQuery);
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
    public async Task GetOpenIssuesAsync_ExactlyAtThePageLimit_ReturnsAllOfThemUntruncated()
    {
        // The boundary AC-519's truncation warning keys on: real parsing of a real page of exactly the limit.
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(_IssuesJson(GitHubIssuesClient.IssuePageLimit)));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, wasTruncated) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal(GitHubIssuesClient.IssuePageLimit, issues.Count);
        Assert.True(wasTruncated);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_ExactlyAtThePageLimitWithSomePullRequestsMixedIn_StillReportsTruncation()
    {
        // AC-519 fix (adversarial review): the raw page can be exactly the limit yet filter down to far fewer real
        // issues once pull requests are stripped out — this is the fixture that reproduces that: 100 raw items, 40
        // of them pull requests, 60 real issues left. WasTruncated must still be true because it is measured on the
        // raw page, before this method's own pull-request filter runs, not on what filtering leaves behind.
        var realIssues = string.Join(",", Enumerable.Range(1, 60).Select(n => $$"""{ "number": {{n}}, "title": "Issue {{n}}", "html_url": "https://x/{{n}}" }"""));
        var pullRequests = string.Join(",", Enumerable.Range(1, 40).Select(n => $$"""{ "number": {{1000 + n}}, "title": "PR {{n}}", "html_url": "https://x/pr/{{n}}", "pull_request": { "url": "https://x/pr/{{n}}" } }"""));
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json($"[{realIssues},{pullRequests}]"));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, wasTruncated) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal(60, issues.Count);
        Assert.True(wasTruncated);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_OneMoreThanThePageLimit_TheServerIsWhatWouldCapIt()
    {
        // gh/GitHub itself does the capping, not this client — asking for 101 back (a server that ignores per_page)
        // proves the client does not impose a second, silent limit of its own on top of the real one.
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(_IssuesJson(GitHubIssuesClient.IssuePageLimit + 1)));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (issues, _) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

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

        var (issues, wasTruncated) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.Equal([1], issues.Select(issue => issue.Number));
        Assert.False(wasTruncated);
    }

    [Fact]
    public async Task GetOpenIssuesAsync_OneShortOfThePageLimit_ReportsNotTruncated()
    {
        using var server = LoopbackServer.Start(_ => LoopbackServer.Json(_IssuesJson(GitHubIssuesClient.IssuePageLimit - 1)));
        GitHubIssuesClient.BaseUrl = server.BaseUrl;

        var (_, wasTruncated) = await new GitHubIssuesClient().GetOpenIssuesAsync("octocat", "hello-world", token: null, assignedToMe: false, CancellationToken.None);

        Assert.False(wasTruncated);
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
