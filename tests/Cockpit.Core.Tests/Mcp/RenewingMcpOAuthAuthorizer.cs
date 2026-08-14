using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// An authorizer that stands in for a renewal actually succeeding: it counts the attempts and writes a new token
/// into the store, which is what the SDK's own token cache does during the connect these options are handed to.
/// <para>
/// The seam is faked deliberately and narrowly. What is under test here is the coordinator's own arithmetic — which
/// margin applies, and whether two renewals can run at once — and none of that lives on the SDK's side of the line.
/// That the SDK really does drive a refresh grant through this path is proved elsewhere, end to end, by
/// <see cref="McpOAuthOfflineAccessFlowTests"/> against a real authorization server.
/// </para>
/// </summary>
/// <param name="store">Where the renewed token lands, exactly as the real token cache would put it there.</param>
/// <param name="lifetime">How long the token this hands out is good for — the knob the margin tests turn.</param>
internal sealed class RenewingMcpOAuthAuthorizer(IMcpOAuthTokenStore store, TimeSpan lifetime) : IMcpOAuthAuthorizer
{
    private int _attempts;

    /// <summary>How many renewals were started. One <c>CreateOptions</c> call is one handshake, so this counts them exactly.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Signalled the moment the first renewal is under way, so a test can open the race deliberately rather than hope for it.</summary>
    public ManualResetEventSlim Started { get; } = new(false);

    /// <summary>Held closed while a test lines up the callers that should end up waiting instead of renewing too.</summary>
    public ManualResetEventSlim Gate { get; } = new(true);

    /// <summary>The token value each renewal produces — different every time, so "everybody got the same one" is a claim a test can check.</summary>
    public string LastIssuedToken { get; private set; } = string.Empty;

    /// <summary>
    /// How long the tokens it hands out live, from now on. Settable so a test can have a server start issuing
    /// longer or shorter ones halfway, which is what the measured lead has to follow.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = lifetime;

    /// <summary>The renewal margin the last caller asked for (AC-771) — the seam this fake stands in for is the one
    /// that has to carry it, so a test can check it arrived rather than infer it.</summary>
    public TimeSpan LastRenewalMargin { get; private set; }

    public ClientOAuthOptions CreateOptions(
        McpServerConfig server,
        bool interactive = true,
        McpSignInStageRecorder? stageRecorder = null,
        TimeSpan renewalMargin = default)
    {
        LastRenewalMargin = renewalMargin;
        var attempt = Interlocked.Increment(ref _attempts);
        Started.Set();
        Gate.Wait();

        var issued = $"renewed-access-token-{attempt}";
        LastIssuedToken = issued;
        store.SaveAsync(
            server.IdentityKey,
            server.Name,
            new McpOAuthToken
            {
                AccessToken = issued,
                RefreshToken = $"rotated-refresh-token-{attempt}",
                ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
                ResourceUrl = server.Url,
            }).GetAwaiter().GetResult();

        // A loopback redirect nothing ever answers: the connect these options are handed to fails at the transport,
        // which is fine — the renewal this stands for has already been written above.
        return new ClientOAuthOptions { RedirectUri = new Uri("http://127.0.0.1:1/callback") };
    }
}
