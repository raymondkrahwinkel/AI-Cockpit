using System.Text.Json;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Layout;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Round-trip for the MCP OAuth tokens (AC-353) in <c>cockpit.json</c>, plus the naming that decides whether they are
/// encrypted at rest — <c>SecretFields</c> works on the field's name, so the names here are not cosmetic.
/// </summary>
public class McpOAuthTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public McpOAuthTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    private static McpOAuthToken _Token(string accessToken = "access") => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh",
        Scheme = "Bearer",
        ExpiresAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
        Scope = "mcp:read",
        ResourceUrl = "https://depot.example/mcp",
        ClientId = "dcr-client",
        ClientSecret = "dcr-secret",
        TokenEndpointAuthMethod = "client_secret_post",
        AuthorizationServer = "https://depot.example",
    };

    [Fact]
    public async Task GetAsync_WithNothingStored_IsNull()
    {
        Assert.Null(await new McpOAuthTokenStore(_configFilePath).GetAsync("depot"));
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsTheToken()
    {
        var store = new McpOAuthTokenStore(_configFilePath);

        await store.SaveAsync("depot", "depot", _Token());
        var loaded = await store.GetAsync("depot");

        Assert.NotNull(loaded);
        Assert.Equal("access", loaded.AccessToken);
        Assert.Equal("refresh", loaded.RefreshToken);
        Assert.Equal("Bearer", loaded.Scheme);
        Assert.Equal("mcp:read", loaded.Scope);
        Assert.Equal("https://depot.example/mcp", loaded.ResourceUrl);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), loaded.ExpiresAt);

        // AC-505: without these surviving the round-trip (this store persists to cockpit.json — the actual survival
        // this ticket's refresh token needs to be worth anything across an app restart), a stored refresh token is
        // dead on arrival on the next cold start.
        Assert.Equal("dcr-client", loaded.ClientId);
        Assert.Equal("dcr-secret", loaded.ClientSecret);
        Assert.Equal("client_secret_post", loaded.TokenEndpointAuthMethod);
        Assert.Equal("https://depot.example", loaded.AuthorizationServer);
    }

    [Fact]
    public async Task SaveAsync_Twice_ReplacesTheTokenRatherThanStackingThem()
    {
        var store = new McpOAuthTokenStore(_configFilePath);

        await store.SaveAsync("depot", "depot", _Token("first"));
        await store.SaveAsync("depot", "depot", _Token("second"));

        // A renewal happens on every refresh; a store that appended would grow a config file full of dead
        // credentials, and leave it ambiguous which one is current.
        Assert.Equal("second", (await store.GetAsync("depot"))?.AccessToken);
    }

    [Fact]
    public async Task GetAsync_FindsATokenAnOlderBuildFiledUnderTheServerName()
    {
        // AC-403 migration, the ordinary upgrade: nothing rewrote this entry, and nothing has to. The store keys on
        // the server's id now, and an entry with no id answers to the id its name derives to — which is exactly the
        // id McpServerEntry hands back for a row that has none of its own either. Without this an operator who
        // upgrades is told to sign in again for a credential that is sitting right there.
        _WriteLegacyTokenFile(serverName: "depot");

        var loaded = await new McpOAuthTokenStore(_configFilePath).GetAsync(McpServerIdentity.LegacyIdFor("depot"));

        Assert.NotNull(loaded);
        Assert.Equal("legacy-access", loaded.AccessToken);
        Assert.Equal("legacy-refresh", loaded.RefreshToken);
    }

    [Fact]
    public async Task GetAsync_DerivesALegacyEntrysIdCaseInsensitively()
    {
        // The name-keyed store matched case-insensitively, so an operator who only changed a server's casing kept
        // their sign-in. That has to survive the move to ids: the derivation lower-cases, which is the one place
        // that rule now lives. A missing lower-case here would strand every token whose stored casing differs.
        _WriteLegacyTokenFile(serverName: "Depot");

        Assert.NotNull(await new McpOAuthTokenStore(_configFilePath).GetAsync(McpServerIdentity.LegacyIdFor("depot")));
    }

    [Fact]
    public async Task GetAsync_DoesNotHandALegacyEntryToADifferentServerThatNowCarriesThatName()
    {
        // The swap this ticket exists for, at the storage layer. A pre-id token filed under "alpha" belongs to
        // whichever server derived its id from "alpha"; a *different* server that has since been renamed to "alpha"
        // has an id of its own and must not reach it. A fallback onto the caller's current name would match here,
        // and on two servers sharing a host that is a bearer sent to an endpoint it was never issued for.
        _WriteLegacyTokenFile(serverName: "alpha");

        var store = new McpOAuthTokenStore(_configFilePath);

        Assert.Null(await store.GetAsync("2f1c4b8e9a7d4e5fb6c3a0d1e2f3a4b5"));
        Assert.Null(await store.GetAsync(McpServerIdentity.LegacyIdFor("beta")));
    }

    [Fact]
    public async Task SaveAsync_WritesTheIdAndDoesNotDisturbALegacyEntryOfAnotherServer()
    {
        _WriteLegacyTokenFile(serverName: "alpha");

        var store = new McpOAuthTokenStore(_configFilePath);
        await store.SaveAsync("2f1c4b8e9a7d4e5fb6c3a0d1e2f3a4b5", "alpha", _Token("minted"));

        // Two servers, both currently called "alpha" as far as the file is concerned, and each keeps its own token:
        // the id is what tells them apart, so neither save nor read can reach across.
        Assert.Equal("minted", (await store.GetAsync("2f1c4b8e9a7d4e5fb6c3a0d1e2f3a4b5"))?.AccessToken);
        Assert.Equal("legacy-access", (await store.GetAsync(McpServerIdentity.LegacyIdFor("alpha")))?.AccessToken);
    }

    [Fact]
    public async Task AdoptLegacyEntriesAsync_MovesAPreIdTokenOntoTheMintedIdItsServerNowCarries()
    {
        // A plugin connection (a Depot one) mints its own id and keeps it across renames, so the id cannot be
        // derived back from the name its token was filed under — the one case the derivation above does not cover.
        // This is the startup pass that closes it, and the only place a token is ever matched to a server's current
        // name; see the interface's own remarks for why that is safe there and nowhere else.
        _WriteLegacyTokenFile(serverName: "Depot: work");

        var store = new McpOAuthTokenStore(_configFilePath);
        await store.AdoptLegacyEntriesAsync(new Dictionary<string, string> { ["Depot: work"] = "connection-id" });

        Assert.Equal("legacy-access", (await store.GetAsync("connection-id"))?.AccessToken);

        // And it moved rather than copied — a second entry answering to the old derivation would be the orphan with
        // a refresh token in it that this ticket is also about.
        Assert.Null(await store.GetAsync(McpServerIdentity.LegacyIdFor("Depot: work")));
    }

    [Fact]
    public async Task AdoptLegacyEntriesAsync_LeavesATokenThatAlreadyCarriesAnIdAlone()
    {
        var store = new McpOAuthTokenStore(_configFilePath);
        await store.SaveAsync("connection-id", "Depot: work", _Token("real-sign-in"));

        // A name can be pointed at a different connection between two launches. The token already filed under an id
        // is the product of an actual sign-in; a name-based guess must never be allowed to overwrite it.
        await store.AdoptLegacyEntriesAsync(new Dictionary<string, string> { ["Depot: work"] = "some-other-id" });

        Assert.Equal("real-sign-in", (await store.GetAsync("connection-id"))?.AccessToken);
        Assert.Null(await store.GetAsync("some-other-id"));
    }

    [Fact]
    public async Task AdoptLegacyEntriesAsync_DoesNotGiveTwoLegacyEntriesTheSameId()
    {
        _WriteLegacyTokenFile(serverName: "Depot: work", secondServerName: "Depot: home");

        var store = new McpOAuthTokenStore(_configFilePath);
        await store.AdoptLegacyEntriesAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Depot: work"] = "connection-id",
            ["Depot: home"] = "connection-id",
        });

        // A caller that hands the same id twice is describing something impossible; the first entry takes it and the
        // second keeps its own derivation rather than both collapsing onto one credential.
        Assert.Equal("legacy-access", (await store.GetAsync("connection-id"))?.AccessToken);
        Assert.NotNull(await store.GetAsync(McpServerIdentity.LegacyIdFor("Depot: home")));
    }

    /// <summary>
    /// Writes the <c>mcpOAuthTokens</c> section exactly as a build before AC-403 left it: a <c>ServerName</c> and no
    /// <c>ServerId</c>. Written as raw JSON on purpose — the store itself can no longer produce this shape, and a
    /// fixture that built it through the store would be testing the store against its own current output rather
    /// than against what is actually on operators' disks.
    /// </summary>
    private void _WriteLegacyTokenFile(string serverName, string? secondServerName = null)
    {
        var entries = new[] { serverName }
            .Concat(secondServerName is null ? [] : new[] { secondServerName })
            .Select(name => $$"""
                {
                  "ServerName": {{JsonSerializer.Serialize(name)}},
                  "AccessToken": "legacy-access",
                  "Scheme": "Bearer",
                  "RefreshToken": "legacy-refresh",
                  "ExpiresAt": "2099-01-01T00:00:00+00:00",
                  "Scope": "mcp:read",
                  "ResourceUrl": "https://depot.example/mcp"
                }
                """);

        File.WriteAllText(_configFilePath, $$"""
            {
              "McpOAuthTokens": [{{string.Join(",", entries)}}]
            }
            """);
    }

    [Fact]
    public async Task RemoveAsync_ForgetsTheToken_AndIsHarmlessWhenThereIsNone()
    {
        var store = new McpOAuthTokenStore(_configFilePath);
        await store.SaveAsync("depot", "depot", _Token());

        await store.RemoveAsync("depot");
        await store.RemoveAsync("depot");

        Assert.Null(await store.GetAsync("depot"));
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        await new McpOAuthTokenStore(_configFilePath).SaveAsync("depot", "depot", _Token());

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
    }

    [Fact]
    public void TheTokenFieldsAreCoveredByTheSecretRule_AndTheSchemeIsNot()
    {
        // This is why the fields are called what they are: encryption and backup-scrubbing both key off the name, so
        // AccessToken/RefreshToken ride the existing "token" rule with no plumbing. The scheme holds the word
        // "Bearer" and is deliberately not called TokenType, which would have it needlessly encrypted.
        Assert.True(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.AccessToken)));
        Assert.True(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.RefreshToken)));
        Assert.False(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.Scheme)));

        // AC-505: ClientSecret rides the same "secret" rule ClientId is deliberately not named to avoid — a client
        // id is not a credential and should stay visible, the way AccessToken/RefreshToken already distinguish
        // themselves from Scheme above.
        Assert.True(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.ClientSecret)));
        Assert.False(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.ClientId)));
        Assert.False(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.AuthorizationServer)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
