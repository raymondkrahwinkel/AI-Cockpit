using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Cockpit.Infrastructure.BackendApi;
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

    // AC-1625 (L2d, secrets): a connect on the server, its browser step on the desktop that asked. The server keeps the
    // verifier, a redirect for another request leaves the sign-in waiting, and no code or token reaches view, log or audit.
    [Fact]
    public async Task AConnectOnTheServer_TakesOnlyItsOwnRedirect_AndNoCodeOrTokenLeavesIt()
    {
        const string Code = "code-from-the-provider-secret";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var signIns = new PluginOAuthSignIns();
        var accounts = new PluginOAuthAccounts(ConfigPath, new HttpClient(_endpoint), _logger, signIns: signIns);
        var said = new List<string>();
        _endpoint.Answer(HttpStatusCode.OK, """{"access_token":"at-connected-secret","refresh_token":"rt-connected-secret","expires_in":3600}""");

        var connecting = Task.Run(() =>
        {
            PluginOAuthSignIns.Relay = new PluginOAuthRelay("onedrive-folder", "desktop1", (provider, outcome) =>
            {
                said.Add($"{provider} sign-in {outcome}");
                return Task.CompletedTask;
            });
            return accounts.ConnectAsync(PluginId, Provider, ["Files.Read"], null, _ => throw new InvalidOperationException("The server opened a browser of its own."), timeout.Token);
        });
        PluginOAuthSignIn? pending;
        while ((pending = signIns.Pending("onedrive-folder", "desktop1")) is null)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Null(signIns.Pending("onedrive-folder", "desktop2"));
        Assert.True(pending.Claim(50123));
        await pending.LinkAsync(TimeSpan.FromSeconds(10), timeout.Token);
        said.Add(JsonSerializer.Serialize(PluginEndpoints.OAuthSignInView(pending)));
        var link = HttpUtility.ParseQueryString(pending.Url?.Query ?? "");
        _endpoint.RequirePkce(Code, link["code_challenge"] ?? "");

        Assert.Equal("http://127.0.0.1:50123/callback", link["redirect_uri"]);
        Assert.False(pending.Submit($"code={Code}&state=another-request"), "A redirect for another request was taken.");
        Assert.True(pending.AwaitsRedirect, "A refused redirect ended the sign-in.");
        Assert.True(pending.Submit($"code={Code}&state={Uri.EscapeDataString(link["state"] ?? "")}"), "The sign-in's own redirect was refused.");
        var connected = await connecting;
        said.Add(JsonSerializer.Serialize(connected));
        said.Add(JsonSerializer.Serialize(PluginEndpoints.OAuthSignInView(pending)));

        Assert.Equal(PluginOAuthOutcome.Ok, connected.Outcome);
        Assert.Contains("microsoft sign-in Ok", said);
        var stored = Assert.Single((await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None))?.PluginOAuthAccounts ?? []);
        Assert.Equal("at-connected-secret", stored.AccessToken);
        var everything = string.Join('\n', said.Concat(_logger.Messages));
        foreach (var secret in new[] { Code, "at-connected-secret", "rt-connected-secret", "cs-client-secret" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
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
        private (string Code, string Challenge)? _pkce;

        public int Posts => Volatile.Read(ref _posts);

        public Task FirstPost => _firstPost.Task;

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        // A token endpoint that redeems only `code` with the verifier behind `challenge` (PKCE, S256).
        public void RequirePkce(string code, string challenge) => _pkce = (code, challenge);

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

            if (_pkce is { } pkce && request.Content is { } content)
            {
                var form = HttpUtility.ParseQueryString(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var verified = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? "")));
                if (form["code"] != pkce.Code || verified != pkce.Challenge)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json") };
                }
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
