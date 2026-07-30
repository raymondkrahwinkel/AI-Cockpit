using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// What a failed sign-in is allowed to claim (AC-457). Found live: pressing <em>Sign in</em> on a server whose
/// discovery document was refused produced "Check the browser window" with no browser anywhere, and the one line
/// that held the reason was written at Information under a sentence saying the operator had not been asked. Both
/// halves are here — the stage that travels back out, and which branch logs at which level.
/// </summary>
public class McpSignInStageTests
{
    // Port 1 is refused immediately rather than left hanging, so the handshake fails deterministically and fast.
    private const string UnreachableUrl = "http://127.0.0.1:1/mcp";

    private static McpServerConfig _OAuthServer() => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = UnreachableUrl,
        Auth = McpServerAuth.OAuth,
    };

    private static McpOAuthToken _StaleToken() => new()
    {
        AccessToken = "expired-access-token",
        RefreshToken = "refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ResourceUrl = UnreachableUrl,
    };

    [Fact]
    public async Task Acquire_WhenTheSignInNeverReachedABrowser_SaysSo()
    {
        var store = new FakeMcpOAuthTokenStore();
        var coordinator = new McpOAuthCoordinator(
            store, new FakeMcpOAuthAuthorizer(), NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: true);

        // The live case: the client rejected the server's protected-resource metadata, so the authorization URL was
        // never known and the code that hands one to a browser was never reached. Anything that then names a window
        // is describing a run that did not happen.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(McpSignInStage.NoBrowserLaunched, access.SignInStage);
    }

    [Theory]
    [InlineData(McpSignInStage.BrowserRequested)]
    [InlineData(McpSignInStage.AuthorizationReturned)]
    public async Task Acquire_CarriesBackHowFarTheSignInGot(McpSignInStage reached)
    {
        var store = new FakeMcpOAuthTokenStore();
        var coordinator = new McpOAuthCoordinator(
            store, new FakeMcpOAuthAuthorizer { StageReached = reached }, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: true);

        // The server is unreachable either way, so the verdict is the same one every time and the stage is the only
        // thing that distinguishes the runs — which is exactly what the operator is told apart by.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(reached, access.SignInStage);
    }

    [Fact]
    public async Task Acquire_AskedByTheOperator_LogsAtWarning_AndNeverSaysTheyWereNotAsked()
    {
        var store = new FakeMcpOAuthTokenStore();
        var logger = new CapturingLogger<McpOAuthCoordinator>();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);

        await new McpOAuthCoordinator(store, authorizer, logger).AcquireAsync(_OAuthServer(), interactive: true);

        // A sign-in the operator started and watched fail is not routine housekeeping, and Information is where a
        // reason goes to be missed — this line is the only place the reason exists at all. Exactly one, because the
        // caller writes its own line for the failures that arrive without an exception: two would mean both fired.
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        // The sentence the non-interactive branch uses describes the branch its author had in mind. On this one it
        // says the opposite of what took place: asking is precisely what the operator did.
        Assert.DoesNotContain(logger.Messages, message => message.Contains("without asking the operator"));
    }

    [Fact]
    public async Task Acquire_AskedByTheOperator_LogsEvenWhenNothingThrew()
    {
        var store = new FakeMcpOAuthTokenStore();
        var logger = new CapturingLogger<McpOAuthCoordinator>();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var withoutAnAddress = new McpServerConfig
        {
            Id = "depot",
            Name = "depot",
            Transport = McpTransport.Http,
            Url = string.Empty,
            Auth = McpServerAuth.OAuth,
        };

        await new McpOAuthCoordinator(store, authorizer, logger).AcquireAsync(withoutAnAddress, interactive: true);

        // There is nothing to connect to, so no handshake runs and nothing throws — and the catch that writes the
        // operator's line never fires. The dialog still tells them the reason is in the log, so a quiet failure has
        // to leave one there too, or the referral sends them to an empty file: the browser window again, relocated.
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Acquire_RenewingWithoutTheOperator_StaysAtInformation()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _StaleToken());
        var logger = new CapturingLogger<McpOAuthCoordinator>();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);

        await new McpOAuthCoordinator(store, authorizer, logger).AcquireAsync(_OAuthServer(), interactive: false);

        // The other half of the split, and the reason the split exists rather than promoting the whole catch: a
        // renewal that cannot happen without a person is the expected outcome on every session start, and raising it
        // to Warning would fill the log with a non-event.
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }
}
