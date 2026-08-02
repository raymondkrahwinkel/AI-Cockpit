using System.Globalization;

namespace Cockpit.Core.Mcp;

// Mints and derives the stable id an MCP server is known by (AC-403). The name an operator types is a label
// they may change at any time; everything that has to keep pointing at the same server across such a change —
// the OAuth token store above all — keys on this instead.
//
// Two shapes, deliberately in disjoint namespaces so one can never be mistaken for the other: a freshly created
// server gets `NewId` (a plain hex GUID, which can never contain a colon), and a server that
// predates this id gets `LegacyIdFor` — derived from the name it carried at the moment the id was
// first needed, so the derivation is *pure*. That purity is the point: a random id minted while reading
// would differ per read, and minting-and-writing-back inside a read path is a write race waiting to happen. A
// derived id needs neither, and survives an older build stripping the id field back out on its own save.
//
// ⚠️ A legacy id is derived from the name *once*, and from then on travels with the row. It is never
// re-derived from the current name: re-deriving is exactly the defect this ticket exists for — two servers that
// swap names would swap tokens with each other.
public static class McpServerIdentity
{
    // Marks an id as derived from a name rather than minted. A colon cannot occur in `NewId`'s
    // output ("N"-format GUIDs are hex only), so the two kinds can never collide.
    private const string LegacyPrefix = "name:";

    // Marks an id handed to a row that could not keep its own because another row had already claimed it. Its own
    // namespace again, for the same reason: nothing minted or derived can land on it, so a credential can never be
    // found under it — which is the point. A duplicate reads as "not signed in" rather than sharing somebody
    // else's token.
    private const string UnmatchablePrefix = "row:";

    // A fresh id for a server being created now.
    public static string NewId() => Guid.NewGuid().ToString("n");

    // An id for the row at `row` that nothing will ever have a token filed under. Derived from the
    // row's position rather than minted, so two reads of the same config agree — a random one here would make the
    // same row key differently on every read.
    public static string UnmatchableIdForRow(int row) => UnmatchablePrefix + row.ToString(CultureInfo.InvariantCulture);

    // The id a server that predates `McpServerConfig.Id` is known by, derived from its name.
    // Trimmed and lower-cased so it lands on the same id the name-keyed store matched case-insensitively before,
    // which is what lets a token written by an older build still be found without rewriting anything.
    public static string LegacyIdFor(string? serverName) =>
        LegacyPrefix + (serverName ?? string.Empty).Trim().ToLowerInvariant();
}
