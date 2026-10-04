
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// The login gate (AC-629). Payloads verbatim from CLI 2.1.226. What most of these pin down is *not blocking*:
// the host calls this synchronously on the UI thread and the CLI costs ~575ms warm, 9.3s cold.
[Collection(nameof(ClaudeLoginStatusTests))]
[CollectionDefinition(nameof(ClaudeLoginStatusTests), DisableParallelization = true)]
public class ClaudeLoginStatusTests
{
    private const string LoggedInPayload =
        """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"a@b.c","subscriptionType":"max"}""";

    private const string LoggedOutPayload =
        """{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}""";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-08T13:00:00Z");

    // AC-1483: `authMethod` as measured on CLI 2.1.274 in a scratch config dir — a subscription login says "claude.ai",
    // ANTHROPIC_API_KEY says "api_key", CLAUDE_CODE_OAUTH_TOKEN and ANTHROPIC_AUTH_TOKEN say "oauth_token". A missing or
    // unmeasured value claims nothing; "loggedIn" alone never means the login renews itself.
    [Theory]
    [InlineData(LoggedInPayload, true, PluginCredentialKind.RenewingLogin)]
    [InlineData(LoggedOutPayload, false, PluginCredentialKind.Unknown)]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty"}""", true, PluginCredentialKind.ApiKey)]
    [InlineData("""{"loggedIn":true,"authMethod":"oauth_token","apiProvider":"firstParty"}""", true, PluginCredentialKind.Login)]
    [InlineData("""{"loggedIn":true,"authMethod":"something_new"}""", true, PluginCredentialKind.Unknown)]
    [InlineData("""{"loggedIn":true}""", true, PluginCredentialKind.Unknown)]
    [InlineData("""{"loggedIn":true,"authMethod":7}""", true, PluginCredentialKind.Unknown)]
    public void ReadLoggedIn_ReadsTheCliesOwnPayloads(string json, bool loggedIn, PluginCredentialKind kind)
    {
        Assert.Equal(loggedIn, ClaudeLoginStatus.ReadLoggedIn(json));
        Assert.Equal(kind, ClaudeLoginStatus.KindOf(ClaudeLoginStatus.ReadStatus(json)?.AuthMethod));
    }

    [Theory]
    // Not a logged-out account. The exit code cannot tell these apart either: 1 means logged out and also
    // means the binary was missing.
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"loggedIn":"yes"}""")]
    public void ReadLoggedIn_RubbishIsUnknown_NotLoggedOut(string json) =>
        Assert.Null(ClaudeLoginStatus.ReadLoggedIn(json));

    // The Manage-profiles list binds this on first paint and never re-reads, so a wrong guess stays visible.

    // A CLI that cannot answer must not become a subprocess per dialog paint.

    // "Could not ask" is not an answer: a CLI that fails must never overwrite a reading it did give earlier.

    // AC-732: after a successful in-app login, the gate must answer "logged in" right away — not on the poll
    // tick after next, once its own 1-minute-old "logged out" reading has aged out.

    // A second refresh while one is in flight must not spawn a second CLI.
}
