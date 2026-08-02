using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

// A labelled `McpServerScope` choice for the MCP-servers dialog's "Available to" picker (#26 scoping).
public sealed record McpServerScopeOption(string Label, string ShortLabel, McpServerScope Scope)
{
    public static IReadOnlyList<McpServerScopeOption> All { get; } =
    [
        new("All providers", "", McpServerScope.All),
        new("Local models only", "local only", McpServerScope.LocalOnly),
        new("Claude Code only", "Claude only", McpServerScope.ClaudeOnly),
    ];

    // True for a scope narrower than `McpServerScope.All`, so the list can tag it.
    public bool IsScoped => Scope != McpServerScope.All;

    public static McpServerScopeOption For(McpServerScope scope) =>
        All.FirstOrDefault(option => option.Scope == scope) ?? All[0];
}
