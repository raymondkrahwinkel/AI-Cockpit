namespace Cockpit.Core.Mcp;

// One header the cockpit sends to an MCP server on top of whatever `McpServerConfig.Auth` arranges
// (AC-354). It exists because `McpServerAuth.ApiKey` can only say `Authorization: Bearer`, and a
// server that wants `X-Api-Key` — or any other scheme — was until now not configurable at all, even by an
// operator willing to paste the value in by hand.
//
// The MCP specification says nothing about custom headers; clients that offer them do so as their own extension.
// The one thing it *is* normative about is that a credential must never travel in the query string, which
// is why there is no equivalent of this for URL parameters.
//
// `Name`: The header's field name, e.g. `X-Api-Key`.
// `Value`: The value to send. Treated as a credential throughout — see `McpHeaderEntry` for why the stored field is named as it is.
public sealed record McpHeader(string Name, string Value)
{
    // Whether this row carries enough to send. A half-filled row is something the operator is still typing, not a header.
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Value);

    // Overrides the generated `ToString()`, which would print the value in the clear. A custom header is in
    // practice always a credential — that is the entire reason this type exists — so it is redacted like any other
    // (Iron Law #8).
    public override string ToString() =>
        $"{nameof(McpHeader)} {{ {nameof(Name)} = {Name}, {nameof(Value)} = {(string.IsNullOrEmpty(Value) ? "null" : "***")} }}";
}
