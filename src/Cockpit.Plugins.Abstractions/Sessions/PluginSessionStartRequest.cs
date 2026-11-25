namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Options for starting a plugin-owned session.</summary>
/// <remarks>
/// An omitted desk uses the active Sessions desk, or the first Sessions desk. An omitted profile uses the named
/// project's default. Set isolation to true to require a worktree, or leave it unset to use the project's default.
/// An omitted MCP selection uses session defaults. An explicit list replaces that selection within the servers
/// selected by the project or profile, contributed by this plugin or required by the brain. Core servers remain mounted.
/// </remarks>
public sealed record PluginSessionStartRequest(
    string? ProfileLabel = null,
    string? Prompt = null,
    string? WorkingDirectory = null,
    string? SessionName = null,
    string? ProjectId = null,
    string? Role = null,
    string? Model = null,
    string? Effort = null,
    IReadOnlyList<string>? McpServers = null,
    bool? IsolateInWorktree = null,
    string? DeskId = null);

/// <summary>The session that started, or the host's refusal reason.</summary>
/// <remarks>PromptDelivered is null when there was no prompt. McpServers lists the selected servers until a connection report confirms which connected.</remarks>
public sealed record PluginSessionStartResult(
    bool Ok,
    string? PaneId,
    string? SessionName,
    string? ResolvedProfileLabel,
    bool? PromptDelivered,
    IReadOnlyList<string>? McpServers,
    string? Error,
    bool McpServersConfirmed = false);
