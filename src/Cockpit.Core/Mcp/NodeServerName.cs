namespace Cockpit.Core.Mcp;

// How a paired node's endpoints are named in the MCP registry (AC-792), as one rule instead of two halves.
//
// A pairing writes one registry row per endpoint the node exposes, named "<node> · <server>". That naming is not
// cosmetic: it is the only record on the controller of *which* rows belong to which node — there is no separate
// store of "who am I paired with" on this side. A pairing repeated has to replace its own rows and no others
// (`_StoreNodeServersAsync`), and AC-795 has to find one node's session server among them again. Writing and
// reading that shape in two places is how the separator drifts and a re-pairing quietly doubles the list.
public static class NodeServerName
{
    // The one separator. Chosen when the rows were first written and now load-bearing, so it lives here rather
    // than inline at either end.
    public const string Separator = " · ";

    // The endpoint AC-795 added: the only one of a node's servers whose tools a controller can actually call, and
    // therefore the row that decides whether a paired node offers session management at all.
    public const string SessionsServerName = "cockpit-node";

    // The registry name for one endpoint on one node.
    public static string For(string nodeName, string serverName) => nodeName + Separator + serverName;

    // The prefix every row for this node starts with — what a re-pairing replaces and nothing else touches.
    public static string PrefixFor(string nodeName) => nodeName + Separator;

    // The node and endpoint a registry name was built from, or null when this is not one of those rows (an
    // operator's own server, named whatever they liked).
    //
    // Splits on the *last* separator, not the first: the endpoint half is the one this side matches on, and it
    // comes from a fixed set this cockpit knows, while the node half is whatever the other machine called itself
    // (`Environment.MachineName`) and is not this side's to constrain. Splitting from the right keeps the half that
    // is checked correct whatever the half that is only displayed turns out to contain.
    public static (string NodeName, string ServerName)? Split(string registryName)
    {
        var at = registryName.LastIndexOf(Separator, StringComparison.Ordinal);
        return at <= 0 || at + Separator.Length >= registryName.Length
            ? null
            : (registryName[..at], registryName[(at + Separator.Length)..]);
    }
}
