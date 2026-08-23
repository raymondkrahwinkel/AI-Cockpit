namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// The answer to <see cref="ICockpitHost.ProbeMcpToolAsync"/> (AC-503): a named <see cref="Outcome"/> plus, only
/// for <see cref="McpProbeOutcome.Success"/>, the tool's own raw text output as <see cref="Detail"/>.
/// </summary>
/// <remarks>
/// Iron Law #8: <see cref="Detail"/> never carries a bearer token or other credential — only the tool's own
/// response text, present only on success.
/// </remarks>
/// <param name="Outcome">
/// What came of the call.
/// </param>
/// <param name="Detail">
/// The tool's raw text output, present only when <paramref name="Outcome"/> is <see cref="McpProbeOutcome.Success"/>.
/// </param>
public sealed record McpProbeResult(McpProbeOutcome Outcome, string? Detail = null)
{
    /// <summary>
    /// The call could not be completed — see <see cref="McpProbeOutcome.Failed"/>.
    /// </summary>
    public static McpProbeResult Failed { get; } = new(McpProbeOutcome.Failed);

    /// <summary>
    /// The server needs a sign-in — see <see cref="McpProbeOutcome.NotSignedIn"/>.
    /// </summary>
    public static McpProbeResult NotSignedIn { get; } = new(McpProbeOutcome.NotSignedIn);

    /// <summary>
    /// The tool reported the value does not resolve — see <see cref="McpProbeOutcome.NotFound"/>.
    /// </summary>
    public static McpProbeResult NotFound { get; } = new(McpProbeOutcome.NotFound);

    /// <summary>
    /// The tool reported success, with its raw text output.
    /// </summary>
    public static McpProbeResult Success(string? detail) => new(McpProbeOutcome.Success, detail);
}
