using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of an <see cref="McpOAuthToken"/> in the <c>mcpOAuthTokens</c> section of <c>cockpit.json</c>.
/// <para>
/// The field names are load-bearing: <c>SecretFields</c> decides what to encrypt and what to empty out of a backup by
/// the name of the field, so <see cref="AccessToken"/> and <see cref="RefreshToken"/> are covered by the existing
/// <c>token</c> rule without any plumbing of their own. The scheme is called <see cref="Scheme"/> rather than
/// <c>TokenType</c> for the same reason, the other way round — it holds the word "Bearer", and a name matching the
/// rule would have it needlessly encrypted.
/// </para>
/// </summary>
internal sealed class McpOAuthTokenEntry
{
    /// <summary>The <see cref="McpServerConfig.Name"/> this token belongs to.</summary>
    public string ServerName { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string Scheme { get; set; } = "Bearer";

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Scope { get; set; }

    /// <summary>The endpoint the token was obtained for, so a server that later answers to the same name under a different address does not inherit it.</summary>
    public string? ResourceUrl { get; set; }

    public static McpOAuthTokenEntry FromDomain(string serverName, McpOAuthToken token) => new()
    {
        ServerName = serverName,
        AccessToken = token.AccessToken,
        Scheme = token.Scheme,
        RefreshToken = token.RefreshToken,
        ExpiresAt = token.ExpiresAt,
        Scope = token.Scope,
        ResourceUrl = token.ResourceUrl,
    };

    public McpOAuthToken ToDomain() => new()
    {
        AccessToken = AccessToken,
        Scheme = Scheme,
        RefreshToken = RefreshToken,
        ExpiresAt = ExpiresAt,
        Scope = Scope,
        ResourceUrl = ResourceUrl,
    };
}
