namespace Cockpit.Core.Mcp;

/// <summary>
/// One header the cockpit sends to an MCP server on top of whatever <see cref="McpServerConfig.Auth"/> arranges
/// (AC-354). It exists because <see cref="McpServerAuth.ApiKey"/> can only say <c>Authorization: Bearer</c>, and a
/// server that wants <c>X-Api-Key</c> — or any other scheme — was until now not configurable at all, even by an
/// operator willing to paste the value in by hand.
/// <para>
/// The MCP specification says nothing about custom headers; clients that offer them do so as their own extension.
/// The one thing it <em>is</em> normative about is that a credential must never travel in the query string, which
/// is why there is no equivalent of this for URL parameters.
/// </para>
/// </summary>
/// <param name="Name">The header's field name, e.g. <c>X-Api-Key</c>.</param>
/// <param name="Value">The value to send. Treated as a credential throughout — see <c>McpHeaderEntry</c> for why the stored field is named as it is.</param>
public sealed record McpHeader(string Name, string Value)
{
    /// <summary>Whether this row carries enough to send. A half-filled row is something the operator is still typing, not a header.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Value);

    /// <summary>
    /// Overrides the generated <c>ToString()</c>, which would print the value in the clear. A custom header is in
    /// practice always a credential — that is the entire reason this type exists — so it is redacted like any other
    /// (Iron Law #8).
    /// </summary>
    public override string ToString() =>
        $"{nameof(McpHeader)} {{ {nameof(Name)} = {Name}, {nameof(Value)} = {(string.IsNullOrEmpty(Value) ? "null" : "***")} }}";
}
