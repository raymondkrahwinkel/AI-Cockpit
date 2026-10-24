using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.OAuth;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// AC-1407: OAuth 2.0 for plugins against any REST provider — a loopback sign-in with PKCE, tokens in `cockpit.json`
// beside the MCP ones, and one refresh per account at a time. No token, code, query or response body is ever logged.
internal sealed partial class PluginOAuthAccounts : ISingletonService
{
    // Renew this long before the provider's expiry, so a call made with the token does not die halfway.
    private static readonly TimeSpan RenewalLead = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly CockpitConfigFileAccess _configFile;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    // One refresh per account (the McpOAuthCoordinator `_renewals` pattern): a second caller waits for the first, since
    // replaying a rotating refresh token is answered with invalid_grant and would cost the operator the account.
    private readonly Dictionary<string, Task<PluginOAuthOutcome>> _refreshes = new(StringComparer.Ordinal);
    private readonly Lock _refreshesLock = new();

    public PluginOAuthAccounts(ILogger<PluginOAuthAccounts> logger)
        : this(CockpitConfigPath.Default, SharedHttp, logger)
    {
    }

    // Test seam: another config file, another key holder, and a token endpoint behind `http`.
    internal PluginOAuthAccounts(string configFilePath, HttpClient http, ILogger logger, ISecretKeyHolder? keyHolder = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath, keyHolder);
        _http = http;
        _logger = logger;
    }

    // Stands in for the system browser in a test; null in production.
    internal Func<Uri, bool>? BrowserOpener { get; init; }

    public async Task<PluginOAuthConnectResult> ConnectAsync(
        string pluginId,
        PluginOAuthProvider provider,
        IReadOnlyList<string> scopes,
        string? accountId,
        CancellationToken cancellationToken)
    {
        if (!provider.IsConfigured)
        {
            return new PluginOAuthConnectResult(PluginOAuthOutcome.NotConfigured);
        }

        if (!_IsHttps(provider.AuthorizationEndpoint) || !_IsHttps(provider.TokenEndpoint))
        {
            _logger.LogWarning("Plugin {PluginId} named a non-https endpoint for OAuth provider {Provider}; not signing in.", pluginId, provider.Id);
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Rejected);
        }

        var existing = accountId is null ? null : await _FindAsync(pluginId, accountId, cancellationToken).ConfigureAwait(false);
        if (accountId is not null && (existing is null || existing.ProviderId != provider.Id))
        {
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Unavailable);
        }

        // Scopes consented to earlier are asked again, so connecting for more never ends up with less.
        List<string> wanted = [.. (existing?.Scopes ?? []).Concat(scopes).Distinct(StringComparer.Ordinal)];
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var loopback = McpOAuthLoopback.Open(_logger);
        if (loopback is null)
        {
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Unreachable);
        }

        var link = _AuthorizationLink(provider, wanted, loopback.RedirectUri, state, challenge);
        if (!(BrowserOpener ?? (url => McpOAuthLoopback.OpenBrowser(url, _logger)))(link))
        {
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        var query = await loopback.ReceiveAsync(provider.Id, state, timeout.Token, "account at").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var (code, _, issuer, error) = McpOAuthLoopback.ParseCallback(query);
        if (query is null || error is not null || string.IsNullOrEmpty(code))
        {
            _logger.LogInformation("The sign-in to OAuth provider {Provider} for plugin {PluginId} did not complete ({Error}).", provider.Id, pluginId, _SafeCode(query is null ? "timeout" : error));
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Cancelled);
        }

        // RFC 9207: an `iss` that is not the host the link went to is a mix-up, whatever the state says.
        if (issuer is not null && !_SameHost(issuer, provider.AuthorizationEndpoint))
        {
            _logger.LogWarning("The sign-in redirect for OAuth provider {Provider} named another issuer; not redeeming it.", provider.Id);
            return new PluginOAuthConnectResult(PluginOAuthOutcome.Rejected);
        }

        var (outcome, token) = await _RedeemAsync(
            provider,
            "authorization_code",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["redirect_uri"] = loopback.RedirectUri.ToString(),
                ["code_verifier"] = verifier,
            },
            cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            // invalid_grant on a code is a bad code, not a dead account.
            return new PluginOAuthConnectResult(outcome == PluginOAuthOutcome.ReconnectRequired ? PluginOAuthOutcome.Rejected : outcome);
        }

        var entry = await _SaveConnectedAsync(pluginId, provider, wanted, token, existing, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Plugin {PluginId} connected an account at OAuth provider {Provider}; its token expires at {ExpiresAt} and {RefreshState}.",
            pluginId,
            provider.Id,
            entry.ExpiresAt,
            string.IsNullOrEmpty(entry.RefreshToken) ? "cannot renew itself" : "can renew itself");
        return new PluginOAuthConnectResult(PluginOAuthOutcome.Ok, _ToAccount(entry, provider));
    }

    public async Task<PluginOAuthTokenResult> GetAccessTokenAsync(
        string pluginId,
        PluginOAuthProvider provider,
        string accountId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        if (!provider.IsConfigured)
        {
            return new PluginOAuthTokenResult(PluginOAuthOutcome.NotConfigured);
        }

        var entry = await _FindAsync(pluginId, accountId, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.ProviderId != provider.Id)
        {
            return new PluginOAuthTokenResult(PluginOAuthOutcome.Unavailable);
        }

        if (_StateOf(entry, provider) == PluginOAuthState.ReconnectRequired)
        {
            return new PluginOAuthTokenResult(PluginOAuthOutcome.ReconnectRequired);
        }

        if (scopes.Except(entry.Scopes, StringComparer.Ordinal).Any())
        {
            return new PluginOAuthTokenResult(PluginOAuthOutcome.ConsentRequired);
        }

        if (_IsFresh(entry))
        {
            return new PluginOAuthTokenResult(PluginOAuthOutcome.Ok, entry.AccessToken);
        }

        var outcome = await _RefreshOnceAsync(pluginId, provider, accountId).WaitAsync(cancellationToken).ConfigureAwait(false);
        var current = await _FindAsync(pluginId, accountId, cancellationToken).ConfigureAwait(false);

        // A provider that is down does not take away a token that still works.
        return current is not null
            && (outcome == PluginOAuthOutcome.Ok || (outcome == PluginOAuthOutcome.Unreachable && current.ExpiresAt > DateTimeOffset.UtcNow))
            ? new PluginOAuthTokenResult(PluginOAuthOutcome.Ok, current.AccessToken)
            : new PluginOAuthTokenResult(outcome == PluginOAuthOutcome.Ok ? PluginOAuthOutcome.Unavailable : outcome);
    }

    public async Task<IReadOnlyList<PluginOAuthAccount>> ListAsync(string pluginId, PluginOAuthProvider provider, CancellationToken cancellationToken)
    {
        var file = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. (file?.PluginOAuthAccounts ?? [])
            .Where(entry => entry.PluginId == pluginId && entry.ProviderId == provider.Id)
            .Select(entry => _ToAccount(entry, provider))];
    }

    public Task DisconnectAsync(string pluginId, string accountId, CancellationToken cancellationToken) =>
        _configFile.UpdateAsync(
            file => file.PluginOAuthAccounts.RemoveAll(entry => entry.PluginId == pluginId && entry.AccountId == accountId),
            cancellationToken);

    private Task<PluginOAuthOutcome> _RefreshOnceAsync(string pluginId, PluginOAuthProvider provider, string accountId)
    {
        lock (_refreshesLock)
        {
            if (!_refreshes.TryGetValue(accountId, out var running))
            {
                // Off-thread, and removed as the last thing the same task does; the removal waits for this lock, so it
                // cannot run before the entry is added. Not tied to the first caller's token: others wait on it too.
                running = Task.Run(async () =>
                {
                    try
                    {
                        return await _RefreshAsync(pluginId, provider, accountId).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_refreshesLock)
                        {
                            _refreshes.Remove(accountId);
                        }
                    }
                });
                _refreshes[accountId] = running;
            }

            return running;
        }
    }

    private async Task<PluginOAuthOutcome> _RefreshAsync(string pluginId, PluginOAuthProvider provider, string accountId)
    {
        // Re-read: a caller that saw the old token may arrive after the refresh it should have waited for has finished.
        var entry = await _FindAsync(pluginId, accountId, CancellationToken.None).ConfigureAwait(false);
        if (entry is null)
        {
            return PluginOAuthOutcome.Unavailable;
        }

        if (_IsFresh(entry))
        {
            return PluginOAuthOutcome.Ok;
        }

        if (_StateOf(entry, provider) == PluginOAuthState.ReconnectRequired || string.IsNullOrEmpty(entry.RefreshToken))
        {
            return PluginOAuthOutcome.ReconnectRequired;
        }

        var (outcome, token) = await _RedeemAsync(
            provider,
            "refresh_token",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refresh_token"] = entry.RefreshToken,
                ["scope"] = string.Join(' ', entry.Scopes),
            },
            CancellationToken.None).ConfigureAwait(false);

        if (token is not null)
        {
            await _UpdateAsync(pluginId, accountId, stored =>
            {
                stored.AccessToken = token.AccessToken;

                // RFC 6749 §6: a refresh answer without a refresh token means "keep the one you have".
                stored.RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? stored.RefreshToken : token.RefreshToken;
                stored.ExpiresAt = token.ExpiresAt;
            }).ConfigureAwait(false);
            _logger.LogInformation("Renewed the access token of a plugin {PluginId} account at OAuth provider {Provider}; it expires at {ExpiresAt}.", pluginId, provider.Id, token.ExpiresAt);
        }
        else if (outcome == PluginOAuthOutcome.ReconnectRequired)
        {
            // Sticky, and the dead grant is dropped: every later call answers from here without asking the provider again.
            await _UpdateAsync(pluginId, accountId, stored =>
            {
                stored.ReconnectRequired = true;
                stored.AccessToken = string.Empty;
                stored.RefreshToken = null;
            }).ConfigureAwait(false);
            _logger.LogWarning("OAuth provider {Provider} revoked the grant of a plugin {PluginId} account; it needs connecting again.", provider.Id, pluginId);
        }

        return outcome;
    }

    // Redeems a grant at the token endpoint and sorts the answer into its failure class. The error code is the only
    // part of a refusal that is logged, and only when it looks like one.
    private async Task<(PluginOAuthOutcome Outcome, IssuedToken? Token)> _RedeemAsync(
        PluginOAuthProvider provider,
        string grantType,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        form["grant_type"] = grantType;
        form["client_id"] = provider.ClientId ?? string.Empty;
        if (!string.IsNullOrEmpty(provider.ClientSecret))
        {
            form["client_secret"] = provider.ClientSecret;
        }

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(provider.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            var body = _ParseObject(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                if (_String(body, "access_token") is { Length: > 0 } accessToken)
                {
                    DateTimeOffset? expiresAt = body is { } json && json.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds) && seconds > 0
                        ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                        : null;
                    return (PluginOAuthOutcome.Ok, new IssuedToken(accessToken, _String(body, "refresh_token"), expiresAt, _String(body, "id_token")));
                }

                _logger.LogWarning("OAuth provider {Provider} accepted a {GrantType} grant but returned no access token.", provider.Id, grantType);
                return (PluginOAuthOutcome.Rejected, null);
            }

            if (status is >= 400 and < 500)
            {
                var error = _String(body, "error");
                _logger.LogWarning("OAuth provider {Provider} refused a {GrantType} grant: {Error} ({Status}).", provider.Id, grantType, _SafeCode(error), status);
                return (error switch
                {
                    "invalid_grant" => PluginOAuthOutcome.ReconnectRequired,
                    "invalid_scope" => PluginOAuthOutcome.ConsentRequired,
                    _ => PluginOAuthOutcome.Rejected,
                }, null);
            }

            _logger.LogWarning("OAuth provider {Provider} failed a {GrantType} grant ({Status}).", provider.Id, grantType, status);
            return (PluginOAuthOutcome.Unreachable, null);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("OAuth provider {Provider} could not be reached for a {GrantType} grant: {Failure}.", provider.Id, grantType, exception.GetType().Name);
            return (PluginOAuthOutcome.Unreachable, null);
        }
    }

    private async Task<PluginOAuthAccountEntry> _SaveConnectedAsync(
        string pluginId,
        PluginOAuthProvider provider,
        List<string> scopes,
        IssuedToken token,
        PluginOAuthAccountEntry? existing,
        CancellationToken cancellationToken)
    {
        var (subject, displayName) = _Identity(token.IdToken);
        PluginOAuthAccountEntry? saved = null;
        await _configFile.UpdateAsync(
            file =>
            {
                var accounts = file.PluginOAuthAccounts;

                // Reconnecting the named account, unless someone else signed in; otherwise the same person found by subject.
                var target = existing is not null && (subject is null || existing.Subject is null || existing.Subject == subject)
                    ? accounts.FirstOrDefault(entry => entry.PluginId == pluginId && entry.AccountId == existing.AccountId)
                    : subject is null
                        ? null
                        : accounts.FirstOrDefault(entry => entry.PluginId == pluginId && entry.ProviderId == provider.Id && entry.Subject == subject);
                if (target is null)
                {
                    target = new PluginOAuthAccountEntry { AccountId = Guid.NewGuid().ToString("N"), PluginId = pluginId, ProviderId = provider.Id };
                    accounts.Add(target);
                }

                // A refresh token is inherited only from a grant issued to this same client at this same endpoint.
                var inheritable = _IsBound(target, provider) ? target.RefreshToken : null;
                target.AccessToken = token.AccessToken;
                target.RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? inheritable : token.RefreshToken;
                target.ExpiresAt = token.ExpiresAt;
                target.Scopes = existing?.AccountId == target.AccountId ? scopes : [.. target.Scopes.Concat(scopes).Distinct(StringComparer.Ordinal)];
                target.ClientId = provider.ClientId ?? string.Empty;
                target.Endpoint = provider.TokenEndpoint.AbsoluteUri;
                target.Subject = subject ?? target.Subject;
                target.DisplayName = displayName ?? target.DisplayName;
                target.ReconnectRequired = false;
                saved = target;
            },
            cancellationToken).ConfigureAwait(false);

        return saved ?? throw new InvalidOperationException("The OAuth account was not saved.");
    }

    private Task _UpdateAsync(string pluginId, string accountId, Action<PluginOAuthAccountEntry> change) =>
        _configFile.UpdateAsync(
            file =>
            {
                if (file.PluginOAuthAccounts.FirstOrDefault(entry => entry.PluginId == pluginId && entry.AccountId == accountId) is { } entry)
                {
                    change(entry);
                }
            },
            CancellationToken.None);

    private async Task<PluginOAuthAccountEntry?> _FindAsync(string pluginId, string accountId, CancellationToken cancellationToken)
    {
        var file = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return file?.PluginOAuthAccounts.FirstOrDefault(entry => entry.PluginId == pluginId && entry.AccountId == accountId);
    }

    private static PluginOAuthAccount _ToAccount(PluginOAuthAccountEntry entry, PluginOAuthProvider provider) =>
        new(entry.AccountId, entry.ProviderId, entry.DisplayName, _StateOf(entry, provider), [.. entry.Scopes]);

    private static PluginOAuthState _StateOf(PluginOAuthAccountEntry entry, PluginOAuthProvider provider) =>
        !provider.IsConfigured ? PluginOAuthState.NotConfigured
        : entry.ReconnectRequired || !_IsBound(entry, provider)
            || (string.IsNullOrEmpty(entry.RefreshToken) && !(entry.ExpiresAt > DateTimeOffset.UtcNow)) ? PluginOAuthState.ReconnectRequired
        : PluginOAuthState.Connected;

    // A changed client id or token endpoint (another app registration, another tenant) makes the held grant foreign.
    private static bool _IsBound(PluginOAuthAccountEntry entry, PluginOAuthProvider provider) =>
        string.Equals(entry.ClientId, provider.ClientId, StringComparison.Ordinal)
        && string.Equals(entry.Endpoint, provider.TokenEndpoint.AbsoluteUri, StringComparison.Ordinal);

    private static bool _IsFresh(PluginOAuthAccountEntry entry) =>
        !string.IsNullOrEmpty(entry.AccessToken) && (entry.ExpiresAt is null || entry.ExpiresAt.Value - RenewalLead > DateTimeOffset.UtcNow);

    private static Uri _AuthorizationLink(PluginOAuthProvider provider, List<string> scopes, Uri redirectUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>(provider.AuthorizationParameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId ?? string.Empty,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var separator = string.IsNullOrEmpty(provider.AuthorizationEndpoint.Query) ? "?" : "&";
        return new Uri(provider.AuthorizationEndpoint.AbsoluteUri + separator
            + string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    // Display and matching only: the id token came straight from the token endpoint over TLS, and nothing is authorised on it.
    private static (string? Subject, string? DisplayName) _Identity(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return (null, null);
        }

        try
        {
            var claims = _ParseObject(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])));
            var subject = _String(claims, "iss") is { } iss && _String(claims, "sub") is { } sub ? $"{iss}|{sub}" : null;
            return (subject, _String(claims, "email") ?? _String(claims, "preferred_username") ?? _String(claims, "name"));
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    private static JsonElement? _ParseObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? _String(JsonElement? element, string property) =>
        element is { } json && json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool _IsHttps(Uri endpoint) => endpoint.IsAbsoluteUri && endpoint.Scheme == Uri.UriSchemeHttps;

    private static bool _SameHost(string issuer, Uri endpoint) =>
        Uri.TryCreate(issuer, UriKind.Absolute, out var issued) && string.Equals(issued.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase);

    // A provider's error code is short and fixed; anything else is not written down, since it could carry what it echoed.
    private static string _SafeCode(string? code) => code is not null && ErrorCode().IsMatch(code) ? code : "unrecognised";

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex ErrorCode();

    private sealed record IssuedToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? IdToken)
    {
        public override string ToString() => $"{nameof(IssuedToken)} {{ {nameof(ExpiresAt)} = {ExpiresAt} }}";
    }
}
