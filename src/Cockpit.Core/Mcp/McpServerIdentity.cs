namespace Cockpit.Core.Mcp;

/// <summary>
/// Mints and derives the stable id an MCP server is known by (AC-403). The name an operator types is a label
/// they may change at any time; everything that has to keep pointing at the same server across such a change —
/// the OAuth token store above all — keys on this instead.
/// <para>
/// Two shapes, deliberately in disjoint namespaces so one can never be mistaken for the other: a freshly created
/// server gets <see cref="NewId"/> (a plain hex GUID, which can never contain a colon), and a server that
/// predates this id gets <see cref="LegacyIdFor"/> — derived from the name it carried at the moment the id was
/// first needed, so the derivation is <em>pure</em>. That purity is the point: a random id minted while reading
/// would differ per read, and minting-and-writing-back inside a read path is a write race waiting to happen. A
/// derived id needs neither, and survives an older build stripping the id field back out on its own save.
/// </para>
/// <para>
/// ⚠️ A legacy id is derived from the name <em>once</em>, and from then on travels with the row. It is never
/// re-derived from the current name: re-deriving is exactly the defect this ticket exists for — two servers that
/// swap names would swap tokens with each other.
/// </para>
/// </summary>
public static class McpServerIdentity
{
    /// <summary>
    /// Marks an id as derived from a name rather than minted. A colon cannot occur in <see cref="NewId"/>'s
    /// output ("N"-format GUIDs are hex only), so the two kinds can never collide.
    /// </summary>
    private const string LegacyPrefix = "name:";

    /// <summary>A fresh id for a server being created now.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// The id a server that predates <see cref="McpServerConfig.Id"/> is known by, derived from its name.
    /// Trimmed and lower-cased so it lands on the same id the name-keyed store matched case-insensitively before,
    /// which is what lets a token written by an older build still be found without rewriting anything.
    /// </summary>
    public static string LegacyIdFor(string? serverName) =>
        LegacyPrefix + (serverName ?? string.Empty).Trim().ToLowerInvariant();
}
