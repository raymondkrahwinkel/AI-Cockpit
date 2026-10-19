using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-1571 B2: a plugin's MCP server signed in on this machine, its browser step on a desktop elsewhere; one per server.
// The token and the PKCE verifier never leave here. A view carries the link, the status and the deadline; the code
// that comes back is handed to the SDK and kept nowhere else, and neither it nor the link is logged.
internal sealed class McpServerSignIns(ILogger<McpServerSignIns>? logger = null) : IHostedService, ISingletonService
{
    // As long as an operator may take over the browser step; then the wait ends and the token held before stays.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(30);

    private readonly ILogger<McpServerSignIns> _logger = logger ?? NullLogger<McpServerSignIns>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FolderId, string Name), McpServerSignIn> _byServer = [];

    // A running sign-in for the same server is ended first. Each one runs only after the one it replaced has ended,
    // chained under the gate, so two never clear and restore the same token store at once.
    public async Task<McpServerSignIn> StartAsync(
        string folderId,
        string name,
        int port,
        Func<CancellationToken, Task<PluginMcpSignInOutcome>> signIn,
        Func<McpServerSignIn, Task> ended,
        CancellationToken cancellationToken)
    {
        var started = new McpServerSignIn(folderId, name, port, DateTimeOffset.UtcNow + Lifetime, _logger);
        McpServerSignIn? replaced;
        lock (_gate)
        {
            _byServer.TryGetValue((folderId, name), out replaced);
            _byServer[(folderId, name)] = started;
            started.Run(replaced?.Ended, signIn, ended);
        }

        replaced?.End("replaced");
        await started.LinkAsync(LinkWait, cancellationToken).ConfigureAwait(false);
        return started;
    }

    public McpServerSignIn? Find(string folderId, string name, string flowId)
    {
        lock (_gate)
        {
            return _byServer.TryGetValue((folderId, name), out var signIn) && string.Equals(signIn.FlowId, flowId, StringComparison.Ordinal)
                ? signIn
                : null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<McpServerSignIn> running;
        lock (_gate)
        {
            running = [.. _byServer.Values];
        }

        foreach (var signIn in running)
        {
            signIn.End("stopped");
        }

        await Task.WhenAll(running.Select(signIn => signIn.Ended)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class McpServerSignIn
{
    private readonly McpOAuthHandoff _handoff;
    private readonly ILogger _logger;
    private volatile string _status = "running";
    private volatile string? _endedAs;

    public McpServerSignIn(string folderId, string name, int port, DateTimeOffset expiresAt, ILogger logger)
    {
        FolderId = folderId;
        Name = name;
        ExpiresAt = expiresAt;
        _handoff = new McpOAuthHandoff(name, port);
        _logger = logger;
    }

    public string FlowId { get; } = Guid.NewGuid().ToString("N");

    public string FolderId { get; }

    public string Name { get; }

    public DateTimeOffset ExpiresAt { get; }

    // running; then succeeded, declined, unavailable or unreachable as the host answered; or expired, replaced, cancelled, stopped.
    public string Status => _status;

    // The authorization link for the desktop's browser, once the SDK built it; only http(s) crosses.
    public Uri? Url => _handoff.Link.IsCompletedSuccessfully && _handoff.Link.Result.Scheme is "https" or "http" ? _handoff.Link.Result : null;

    public bool AwaitsRedirect => _status == "running" && Url is not null && !_handoff.Answered;

    internal Task Ended { get; private set; } = Task.CompletedTask;

    // False when the redirect is not the answer to this sign-in's own link (see McpOAuthHandoff.Submit).
    public bool Submit(string query) => _status == "running" && _handoff.Submit(query);

    // Ends it unsigned; the token held before is put back.
    internal void End(string status)
    {
        _endedAs ??= status;
        _handoff.Abandon();
    }

    internal Task LinkAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        Task.WhenAny(_handoff.Link, Ended, Task.Delay(wait, cancellationToken));

    // Not awaited here: the sign-in outlives the request that started it. _RunAsync catches everything it can meet.
    internal void Run(Task? predecessor, Func<CancellationToken, Task<PluginMcpSignInOutcome>> signIn, Func<McpServerSignIn, Task> ended) =>
        Ended = _RunAsync(predecessor ?? Task.CompletedTask, signIn, ended);

    private async Task _RunAsync(Task predecessor, Func<CancellationToken, Task<PluginMcpSignInOutcome>> signIn, Func<McpServerSignIn, Task> ended)
    {
        await Task.Yield();
        try
        {
            await predecessor.ConfigureAwait(false);

            // Abandoning rather than cancelling: a cancelled handshake skips the coordinator's restore of the token it held.
            using var expiry = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(0, (ExpiresAt - DateTimeOffset.UtcNow).Ticks)));
            using var registration = expiry.Token.Register(() => End("expired"));
            var outcome = _endedAs is null ? await _SignInAsync(signIn).ConfigureAwait(false) : PluginMcpSignInOutcome.Declined;
            _status = outcome == PluginMcpSignInOutcome.Authorized ? "succeeded" : _endedAs ?? outcome switch
            {
                PluginMcpSignInOutcome.Unavailable => "unavailable",
                PluginMcpSignInOutcome.Unreachable => "unreachable",
                _ => "declined",
            };
        }
        catch (Exception exception)
        {
            _status = _endedAs ?? "declined";
            _logger.LogWarning("The sign-in for MCP server {Server} failed ({Error}).", Name, exception.GetType().Name);
        }
        finally
        {
            _handoff.Abandon();
        }

        try
        {
            await ended(this).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Ending the sign-in for MCP server {Server} failed ({Error}).", Name, exception.GetType().Name);
        }
    }

    // Its own async method, so the handoff set here is seen by this sign-in's flow and nothing that outlives it.
    private async Task<PluginMcpSignInOutcome> _SignInAsync(Func<CancellationToken, Task<PluginMcpSignInOutcome>> signIn)
    {
        McpOAuthHandoff.Current = _handoff;
        return await signIn(CancellationToken.None).ConfigureAwait(false);
    }
}
