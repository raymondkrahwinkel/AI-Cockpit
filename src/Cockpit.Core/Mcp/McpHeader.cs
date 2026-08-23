namespace Cockpit.Core.Mcp;

// AC-354: one header sent to an MCP server on top of whatever `McpServerConfig.Auth` arranges, since
// `McpServerAuth.ApiKey` can only say `Authorization: Bearer`. `Value` is treated as a credential throughout —
// see `McpHeaderEntry` for why the stored field is named as it is.
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
