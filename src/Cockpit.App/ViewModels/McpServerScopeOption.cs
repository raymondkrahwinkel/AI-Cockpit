using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

/// <summary>A labelled <see cref="McpServerScope"/> choice for the MCP-servers dialog's "Available to" picker (#26 scoping).</summary>
public sealed record McpServerScopeOption(string Label, string ShortLabel, McpServerScope Scope)
{
    public static IReadOnlyList<McpServerScopeOption> All { get; } =
    [
        new("All providers", "", McpServerScope.All),
        new("Local models only", "local only", McpServerScope.LocalOnly),
        new("Claude Code only", "Claude only", McpServerScope.ClaudeOnly),
    ];

    /// <summary>True for a scope narrower than <see cref="McpServerScope.All"/>, so the list can tag it.</summary>
    public bool IsScoped => Scope != McpServerScope.All;

    public static McpServerScopeOption For(McpServerScope scope) =>
        All.FirstOrDefault(option => option.Scope == scope) ?? All[0];
}
