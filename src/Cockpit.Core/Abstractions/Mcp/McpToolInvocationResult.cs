namespace Cockpit.Core.Abstractions.Mcp;

// Named outcomes for `IMcpToolInvoker.InvokeAsync` — see each factory for what produces it.
public enum McpToolInvocationOutcome
{
    // Failed is deliberately the zero value: default(McpToolInvocationOutcome) — an unstubbed fake, a missed
    // switch arm — must never read as a usable result, the same defensive reasoning PluginMcpSignInOutcome's own
    // doc comment gives for keeping Unavailable at zero there.

    // The server could not be reached, the tool call errored, or the server/tool is unknown. See `McpToolInvocationResult.Error`.
    Failed,

    // The tool ran and returned `McpToolInvocationResult.Content`.
    Success,

    // The server is OAuth-protected and has no usable token yet — an interactive sign-in is needed first.
    AuthorizationRequired,
}

// One call's result — success with the tool's own text content, or a named reason it did not run.
public sealed record McpToolInvocationResult(McpToolInvocationOutcome Outcome, string? Content, string? Error)
{
    public static McpToolInvocationResult Success(string content) => new(McpToolInvocationOutcome.Success, content, null);

    public static McpToolInvocationResult AuthorizationRequired { get; } = new(McpToolInvocationOutcome.AuthorizationRequired, null, null);

    public static McpToolInvocationResult Failed(string error) => new(McpToolInvocationOutcome.Failed, null, error);
}
