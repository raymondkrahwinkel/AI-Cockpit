namespace Cockpit.Core.Mcp;

/// <summary>The Core-level mirror of <c>Cockpit.Plugins.Abstractions.Mcp.McpProbeResult</c> — see <see cref="McpToolProbeOutcome"/>'s own remarks on why this is a separate type rather than a shared one.</summary>
/// <param name="Outcome">What came of the call.</param>
/// <param name="Detail">The tool's raw text output, present only when <paramref name="Outcome"/> is <see cref="McpToolProbeOutcome.Success"/>.</param>
public sealed record McpToolProbeResult(McpToolProbeOutcome Outcome, string? Detail = null)
{
    /// <summary>The call could not be completed.</summary>
    public static McpToolProbeResult Failed { get; } = new(McpToolProbeOutcome.Failed);

    /// <summary>The server needs a sign-in.</summary>
    public static McpToolProbeResult NotSignedIn { get; } = new(McpToolProbeOutcome.NotSignedIn);

    /// <summary>The tool reported the value does not resolve.</summary>
    public static McpToolProbeResult NotFound { get; } = new(McpToolProbeOutcome.NotFound);

    /// <summary>The tool reported success, with its raw text output.</summary>
    public static McpToolProbeResult Success(string? detail) => new(McpToolProbeOutcome.Success, detail);
}
