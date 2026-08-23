namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// The answer to <see cref="ICockpitHost.ProbeMcpToolAsync"/> (AC-503): a named <see cref="Outcome"/> plus, only for
/// <see cref="McpProbeOutcome.Success"/>, the tool's own raw text output as <see cref="Detail"/> — never a
/// structured re-parse of it, which this host has no way to verify against every possible server's schema.
/// <para>
/// Iron Law #8: whatever builds this never puts a bearer token or other credential in <see cref="Detail"/> — the
/// value here is either absent (every outcome but <see cref="McpProbeOutcome.Success"/>) or the tool's own response
/// text, which this host never mixes with the Authorization header it sent to get there.
/// </para>
/// </summary>
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
