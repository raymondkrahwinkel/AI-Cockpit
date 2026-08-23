namespace Cockpit.Core.Mcp;

// AC-792: how a paired node's endpoints are named in the MCP registry ("<node> · <server>"), as one rule. Not
// cosmetic — it is the only record of which rows belong to which node, used by `_StoreNodeServersAsync` to
// replace a re-pairing's own rows and by AC-795 to find a node's session server, so the shape must live in one place.
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

    // The node and endpoint a registry name was built from, or null when not one of those rows. Splits on the
    // *last* separator: the endpoint half comes from a fixed known set, while the node half
    // (`Environment.MachineName`) is unconstrained, so splitting from the right keeps the checked half correct.
    public static (string NodeName, string ServerName)? Split(string registryName)
    {
        var at = registryName.LastIndexOf(Separator, StringComparison.Ordinal);
        return at <= 0 || at + Separator.Length >= registryName.Length
            ? null
            : (registryName[..at], registryName[(at + Separator.Length)..]);
    }
}
