namespace Cockpit.Core.Mcp;

// Which session worlds a registry MCP server fans out to (#26). The two worlds have very different
// built-in tools: a local model (Ollama/LM Studio) has none, so it needs servers like filesystem, while
// Claude Code already ships file/shell/web tools of its own — the same server there is redundant noise.
// Scoping a server lets one shared registry serve both without cross-contaminating them.
public enum McpServerScope
{
    // Available to every session — both the local-model tool-loop and Claude Code.
    All,

    // Only exposed to local models (Ollama/LM Studio); never fanned out to Claude Code.
    LocalOnly,

    // Only fanned out to Claude Code; never hosted in the local-model tool-loop.
    ClaudeOnly,
}
