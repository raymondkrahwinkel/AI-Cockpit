namespace Cockpit.Core.Mcp;

// The Core-level mirror of `Cockpit.Plugins.Abstractions.Mcp.McpProbeResult` — see `McpToolProbeOutcome`'s own
// remarks on why this is a separate type. `Detail` is the tool's raw text output, present only when `Outcome`
// is `McpToolProbeOutcome.Success`.
public sealed record McpToolProbeResult(McpToolProbeOutcome Outcome, string? Detail = null)
{
    // The call could not be completed.
    public static McpToolProbeResult Failed { get; } = new(McpToolProbeOutcome.Failed);

    // The server needs a sign-in.
    public static McpToolProbeResult NotSignedIn { get; } = new(McpToolProbeOutcome.NotSignedIn);

    // The tool reported the value does not resolve.
    public static McpToolProbeResult NotFound { get; } = new(McpToolProbeOutcome.NotFound);

    // The tool reported success, with its raw text output.
    public static McpToolProbeResult Success(string? detail) => new(McpToolProbeOutcome.Success, detail);
}
