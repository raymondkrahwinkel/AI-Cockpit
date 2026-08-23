namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// Named outcomes for <see cref="ICockpitHost.CallMcpToolAsync"/> — the plugin-facing mirror of the host's own
/// internal <c>McpToolInvocationResult</c>, without a <c>Cockpit.Core</c> type in the signature (see the isolation
/// note on <see cref="ICockpitHost"/>).
/// </summary>
public enum PluginMcpToolCallOutcome
{
    // Unavailable is deliberately the zero value — the same defensive reasoning PluginMcpSignInOutcome's own doc
    // comment gives for keeping its own zero value out of "it worked": an unstubbed fake or a host that predates
    // this member must never read as a usable result.

    /// <summary>
    /// This host predates <see cref="ICockpitHost.CallMcpToolAsync"/>, or the named server is not configured.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The tool ran and returned <see cref="PluginMcpToolCallResult.Content"/>.
    /// </summary>
    Success,

    /// <summary>
    /// The server is OAuth-protected and has no usable token yet — offer <see cref="ICockpitHost.SignInMcpServerAsync"/>.
    /// </summary>
    AuthorizationRequired,

    /// <summary>
    /// The call was attempted but failed — see <see cref="PluginMcpToolCallResult.Error"/>.
    /// </summary>
    Failed,
}

/// <summary>
/// What came of calling one tool on an MCP server this plugin contributed (AC-502) — a named outcome plus the
/// tool's own text content on success, never a bearer token (Iron Law #8 holds here too).
/// </summary>
public sealed record PluginMcpToolCallResult(PluginMcpToolCallOutcome Outcome, string? Content, string? Error)
{
    public static PluginMcpToolCallResult Unavailable { get; } = new(PluginMcpToolCallOutcome.Unavailable, null, null);

    public static PluginMcpToolCallResult Success(string content) => new(PluginMcpToolCallOutcome.Success, content, null);

    public static PluginMcpToolCallResult AuthorizationRequired { get; } = new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null);

    public static PluginMcpToolCallResult Failed(string error) => new(PluginMcpToolCallOutcome.Failed, null, error);
}
