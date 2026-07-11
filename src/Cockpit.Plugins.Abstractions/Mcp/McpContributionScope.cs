namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// Which session worlds a plugin-contributed MCP server (<see cref="ICockpitHost.AddMcpServer"/>, #60) fans
/// out to. Mirrors the host's own <c>Cockpit.Core.Mcp.McpServerScope</c> one-for-one, but lives here so a
/// plugin can express it without referencing <c>Cockpit.Core</c> — see the isolation note on
/// <see cref="ICockpitHost"/>. The host's implementation maps this to its own enum by name, not by ordinal,
/// so the two are free to diverge in declaration order without silently mis-mapping.
/// </summary>
public enum McpContributionScope
{
    /// <summary>Available to every session — both the local-model tool-loop and Claude Code.</summary>
    All,

    /// <summary>Only exposed to local models (Ollama/LM Studio); never fanned out to Claude Code.</summary>
    LocalOnly,

    /// <summary>Only fanned out to Claude Code; never hosted in the local-model tool-loop.</summary>
    ClaudeOnly,
}
