using System.Globalization;

namespace Cockpit.Core.Mcp;

// AC-403: mints and derives the stable id an MCP server is known by, since the operator-typed name is a label
// that can change. `NewId` and `LegacyIdFor` stay in disjoint namespaces; a legacy id is never re-derived from
// a current name, since re-deriving is the swapped-tokens bug this ticket fixes.
public static class McpServerIdentity
{
    // Marks an id as derived from a name rather than minted. A colon cannot occur in `NewId`'s
    // output ("N"-format GUIDs are hex only), so the two kinds can never collide.
    private const string LegacyPrefix = "name:";

    // Marks an id handed to a row whose own id was already claimed by another row. Its own namespace so nothing
    // minted or derived can ever land on it — a duplicate reads as "not signed in" rather than stealing a token.
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
