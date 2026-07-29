namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>Named outcomes for <see cref="IMcpToolInvoker.InvokeAsync"/> — see each factory for what produces it.</summary>
public enum McpToolInvocationOutcome
{
    // Failed is deliberately the zero value: default(McpToolInvocationOutcome) — an unstubbed fake, a missed
    // switch arm — must never read as a usable result, the same defensive reasoning PluginMcpSignInOutcome's own
    // doc comment gives for keeping Unavailable at zero there.

    /// <summary>The server could not be reached, the tool call errored, or the server/tool is unknown. See <see cref="McpToolInvocationResult.Error"/>.</summary>
    Failed,

    /// <summary>The tool ran and returned <see cref="McpToolInvocationResult.Content"/>.</summary>
    Success,

    /// <summary>The server is OAuth-protected and has no usable token yet — an interactive sign-in is needed first.</summary>
    AuthorizationRequired,
}

/// <summary>One call's result — success with the tool's own text content, or a named reason it did not run.</summary>
public sealed record McpToolInvocationResult(McpToolInvocationOutcome Outcome, string? Content, string? Error)
{
    public static McpToolInvocationResult Success(string content) => new(McpToolInvocationOutcome.Success, content, null);

    public static McpToolInvocationResult AuthorizationRequired { get; } = new(McpToolInvocationOutcome.AuthorizationRequired, null, null);

    public static McpToolInvocationResult Failed(string error) => new(McpToolInvocationOutcome.Failed, null, error);
}
