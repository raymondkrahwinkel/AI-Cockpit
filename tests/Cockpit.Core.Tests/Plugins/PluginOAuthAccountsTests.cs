using System.Net;
using System.Text;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.OAuth;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Host OAuth for plugins (AC-1407) against a token endpoint that answers in-process: what is logged, what
/// <c>invalid_grant</c> does to an account, and how many refreshes two callers cost.
/// </summary>
public class PluginOAuthAccountsTests : IDisposable
{
    private const string PluginId = "onedrive";
    private const string AccountId = "account-1";

    private static readonly PluginOAuthProvider Provider = new(
        "microsoft",
        new Uri("https://login.example/oauth2/v2.0/authorize"),
        new Uri("https://login.example/oauth2/v2.0/token"),
        "the-client-id",
        "cs-client-secret");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly TokenEndpoint _endpoint = new();
    private readonly CapturingLogger<PluginOAuthAccounts> _logger = new();

    public PluginOAuthAccountsTests() => Directory.CreateDirectory(_tempDir);

    private string ConfigPath => Path.Combine(_tempDir, "cockpit.json");

    // An account whose access token is past its renewal lead, with a refresh grant behind it.
    private async Task<PluginOAuthAccounts> _AccountsWithAStaleTokenAsync()
    {
        await new CockpitConfigFileAccess(ConfigPath).UpdateAsync(
            file => file.PluginOAuthAccounts.Add(new PluginOAuthAccountEntry
            {
                AccountId = AccountId,
                PluginId = PluginId,
                ProviderId = Provider.Id,
                AccessToken = "at-stale-secret",
                RefreshToken = "rt-original-secret",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                Scopes = ["Files.Read"],
                ClientId = "the-client-id",
                Endpoint = Provider.TokenEndpoint.AbsoluteUri,
            }),
            CancellationToken.None);
        return new PluginOAuthAccounts(ConfigPath, new HttpClient(_endpoint), _logger);
    }

    [Fact]
    public async Task NoTokenOrSecretReachesTheLog_ThroughARenewalAndARevokedGrant()
    {
        var accounts = await _AccountsWithAStaleTokenAsync();

        // A renewal that comes back short-lived, so the next call renews again and meets a revoked grant.
        _endpoint.Answer(HttpStatusCode.OK, """{"access_token":"at-renewed-secret","refresh_token":"rt-rotated-secret","expires_in":60}""");
        var renewed = await accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);
        _endpoint.Answer(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"rt-rotated-secret is revoked"}""");
        var revoked = await accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);

        Assert.Equal(new PluginOAuthTokenResult(PluginOAuthOutcome.Ok, "at-renewed-secret"), renewed);
        Assert.Equal(PluginOAuthOutcome.ReconnectRequired, revoked.Outcome);

        // Both renewals left a line, so the absence below is about what they said, not about saying nothing.
        var log = string.Join('\n', _logger.Messages);
        Assert.Contains("invalid_grant", log, StringComparison.Ordinal);
        foreach (var secret in new[] { "at-stale-secret", "rt-original-secret", "at-renewed-secret", "rt-rotated-secret", "cs-client-secret" })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("at-renewed-secret", renewed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidGrant_MarksTheAccountForReconnecting_AndNeverAsksTheProviderAgain()
    {
        var accounts = await _AccountsWithAStaleTokenAsync();
        _endpoint.Answer(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var first = await accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);
        var second = await accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);
        var listed = Assert.Single(await accounts.ListAsync(PluginId, Provider, CancellationToken.None));

        // The grant is dead: one refusal is the whole conversation, and the UI shows "connect again" from then on.
        Assert.Equal(PluginOAuthOutcome.ReconnectRequired, first.Outcome);
        Assert.Equal(PluginOAuthOutcome.ReconnectRequired, second.Outcome);
        Assert.Equal(PluginOAuthState.ReconnectRequired, listed.State);
        Assert.Equal(1, _endpoint.Posts);
    }

    [Fact]
    public async Task TwoCallersOnAStaleAccount_ShareOneRefresh()
    {
        var accounts = await _AccountsWithAStaleTokenAsync();
        _endpoint.Answer(HttpStatusCode.OK, """{"access_token":"at-renewed-secret","refresh_token":"rt-rotated-secret","expires_in":3600}""");
        _endpoint.HoldAnswers();

        var first = accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);
        await _endpoint.FirstPost.WaitAsync(TimeSpan.FromSeconds(10));
        var second = accounts.GetAccessTokenAsync(PluginId, Provider, AccountId, ["Files.Read"], CancellationToken.None);

        // The second caller arrives while the first refresh is on the wire; a rotating provider answers a replayed
        // refresh token with invalid_grant, so a second POST here would cost the operator the account.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        _endpoint.Release();

        Assert.Equal("at-renewed-secret", (await first).AccessToken);
        Assert.Equal("at-renewed-secret", (await second).AccessToken);
        Assert.Equal(1, _endpoint.Posts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _hold;
        private HttpStatusCode _status;
        private string _body = "{}";
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public Task FirstPost => _firstPost.Task;

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        public void HoldAnswers() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _hold?.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            _firstPost.TrySetResult();
            if (_hold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
