using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of an <see cref="McpServerConfig"/> in the <c>mcpServers</c> section of <c>cockpit.json</c>.</summary>
internal sealed class McpServerEntry
{
    /// <summary>
    /// The server's stable id (AC-403), under which its OAuth token is filed. Empty for a row written before this
    /// field existed — see <see cref="ToDomain"/> for the id such a row answers to instead.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public McpTransport Transport { get; set; }

    public McpServerScope Scope { get; set; } = McpServerScope.All;

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public string? Url { get; set; }

    public McpServerAuth Auth { get; set; }

    public string? ApiKey { get; set; }

    public string? OAuthAuthority { get; set; }

    public string? OAuthClientId { get; set; }

    public string? OAuthScopes { get; set; }

    /// <summary>
    /// Nullable and left out when there are none, the way <c>ProjectEntry.AdditionalInfo</c> is: most servers carry no
    /// custom headers, and writing <c>"Headers": []</c> into every entry is noise in a file the operator reads and
    /// hand-edits. Nullable also because a hand-edited config can put null here and the deserializer will assign it.
    /// </summary>
    public List<McpHeaderEntry>? Headers { get; set; }

    public bool Enabled { get; set; } = true;

    public static McpServerEntry FromDomain(McpServerConfig server) => new()
    {
        // IdentityKey rather than Id, so the id a legacy row was read under is written back explicitly on the first
        // save. From then on it travels with the row, which is what makes the rename after it safe: the id stops
        // being a function of a name that is about to change.
        Id = server.IdentityKey,
        Name = server.Name,
        Transport = server.Transport,
        Scope = server.Scope,
        Command = server.Command,
        Args = [.. server.Args],
        Url = server.Url,
        Auth = server.Auth,
        ApiKey = server.ApiKey,
        OAuthAuthority = server.OAuthAuthority,
        OAuthClientId = server.OAuthClientId,
        OAuthScopes = server.OAuthScopes,
        Headers = server.Headers.Count == 0 ? null : [.. server.Headers.Select(McpHeaderEntry.FromDomain)],
        Enabled = server.Enabled,
    };

    public McpServerConfig ToDomain() => new()
    {
        // A row from before this field derives its id from the name it is carrying right now — the same id the
        // token an older build filed under that name answers to, so the sign-in survives the upgrade without
        // anything being rewritten. Derived, not minted: a random id here would differ on every read.
        Id = string.IsNullOrEmpty(Id) ? McpServerIdentity.LegacyIdFor(Name) : Id,
        Name = Name,
        Transport = Transport,
        Scope = Scope,
        Command = Command,
        Args = Args,
        Url = Url,
        Auth = Auth,
        ApiKey = ApiKey,
        OAuthAuthority = OAuthAuthority,
        OAuthClientId = OAuthClientId,
        OAuthScopes = OAuthScopes,
        // A hand-edited config can leave a row half-written; an incomplete header is dropped rather than sent as a
        // blank field name, which some servers answer with a protocol error rather than a useful message.
        Headers = [.. (Headers ?? []).Select(entry => entry.ToDomain()).Where(header => header.IsComplete)],
        Enabled = Enabled,
    };
}
