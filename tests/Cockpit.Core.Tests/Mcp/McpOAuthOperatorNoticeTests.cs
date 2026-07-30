using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// What the operator actually gets to see when an OAuth-protected MCP server stops working (AC-524).
/// <para>
/// This is the half of the ticket that was reported by a person rather than found in a log: Depot vanished from
/// sessions and nothing anywhere said so. A line written to a file is not a notification — it only reaches someone
/// who already suspects there is something to look for, which is precisely what was missing.
/// </para>
/// </summary>
public class McpOAuthOperatorNoticeTests
{
    private const string ServerUrl = "http://127.0.0.1:1/mcp";

    private static McpServerConfig _Server() => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = ServerUrl,
        Auth = McpServerAuth.OAuth,
    };

    private static McpOAuthToken _StaleToken() => new()
    {
        AccessToken = "stored-access-token",
        RefreshToken = "refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ResourceUrl = ServerUrl,
    };

    [Fact]
    public async Task WhenAServerBecomesUnusable_TheOperatorIsToldTheCauseAndWhatToDo()
    {
        var store = new FakeMcpOAuthTokenStore();
        var toasts = new CapturingToastNotifier();
        var coordinator = new McpOAuthCoordinator(store, new FakeMcpOAuthAuthorizer(), NullLogger<McpOAuthCoordinator>.Instance, toasts);

        await coordinator.AcquireForSessionAsync(_Server());

        Assert.True(await toasts.WaitForAsync(1, TimeSpan.FromSeconds(5)));
        var shown = Assert.Single(toasts.Shown);

        // Nothing is stored, so there is nothing to renew and nothing was ever signed in. The action has to say
        // that: telling someone their sign-in expired sends them looking for one that never existed.
        Assert.Contains("depot", shown.Body, StringComparison.Ordinal);
        Assert.Contains("never signed in", shown.Body, StringComparison.Ordinal);
        Assert.Contains("Sign in", shown.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheSameFailureKeepsHappening_TheOperatorIsToldOnce()
    {
        var store = new FakeMcpOAuthTokenStore();
        var toasts = new CapturingToastNotifier();
        var coordinator = new McpOAuthCoordinator(store, new FakeMcpOAuthAuthorizer(), NullLogger<McpOAuthCoordinator>.Instance, toasts);

        await coordinator.AcquireForSessionAsync(_Server());
        Assert.True(await toasts.WaitForAsync(1, TimeSpan.FromSeconds(5)));
        await coordinator.AcquireForSessionAsync(_Server());
        await coordinator.AcquireForSessionAsync(_Server());

        // The loopback endpoint runs this path on every single call an agent makes. A notification per failure is a
        // desktop nobody can work on, and the fastest way to teach someone to dismiss the one that mattered.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Single(toasts.Shown);
    }

    [Fact]
    public async Task WhenAServerRecoversAndFailsAgain_TheOperatorIsToldAgain()
    {
        var store = new FakeMcpOAuthTokenStore();
        var toasts = new CapturingToastNotifier();
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(6));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance, toasts);

        // Fail — nothing is stored at all.
        await coordinator.AcquireForSessionAsync(_Server());
        Assert.True(await toasts.WaitForAsync(1, TimeSpan.FromSeconds(5)));

        // Recover: a stale token with a refresh grant behind it, which this authorizer renews successfully.
        await store.SaveAsync("depot", "depot", _StaleToken());
        Assert.Equal(McpAuthState.Authorized, (await coordinator.AcquireForSessionAsync(_Server())).State);

        // Fail again, on the same cause as the first time.
        await store.RemoveAsync("depot");
        await coordinator.AcquireForSessionAsync(_Server());

        // "Once per transition" has to mean the latch is cleared by success, or the second outage is the one nobody
        // hears about — and an alert that goes quiet after the first time it fires is worse than none, because it
        // is trusted.
        Assert.True(await toasts.WaitForAsync(2, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, toasts.Shown.Count);
    }

    [Fact]
    public async Task WhenTheDesktopCannotShowANotification_TheCredentialPathIsUnaffected()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _StaleToken());
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(6));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance, new ThrowingToastNotifier());

        // A toast that cannot be shown must never take down the thing it is reporting on, and the failure runs on a
        // task nothing awaits — so an unhandled one would be a silence of its own rather than a visible fault.
        await coordinator.AcquireForSessionAsync(_Server());
        await store.RemoveAsync("depot");
        var access = await coordinator.AcquireForSessionAsync(_Server());

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(McpOAuthAttentionReason.NeverSignedIn, access.Reason);
    }
}
