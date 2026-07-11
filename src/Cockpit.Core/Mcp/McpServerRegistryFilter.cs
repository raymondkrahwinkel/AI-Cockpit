namespace Cockpit.Core.Mcp;

/// <summary>
/// Applies the per-session MCP-server selection (#44) on top of the registry's own
/// <see cref="McpServerConfig.Enabled"/>/<see cref="McpServerScope"/> filtering. The New-session dialog
/// lets the operator uncheck individual registry servers for just that session; both consumers of the
/// shared registry — <c>McpToolProvider</c> (local-model tool-loop) and <c>ClaudeCliProcess</c>'s
/// <c>--mcp-config</c> fan-out — run the registry through this before their own filtering, so the
/// per-session set is a pure narrowing, never a way to reach a server the registry itself disabled.
/// </summary>
public static class McpServerRegistryFilter
{
    /// <summary>
    /// Drops the <em>enabled</em> registry servers not named in <paramref name="enabledServerNames"/>.
    /// <see langword="null"/> means no session-level restriction was made (e.g. the New-session dialog
    /// found no registry servers to offer), so the full registry passes through unchanged. An already
    /// disabled entry always passes through untouched — the New-session checklist only ever offers
    /// <em>enabled</em> registry servers, so a disabled one (including one that deliberately overrides and
    /// suppresses a local-model built-in default of the same name, #26) was never part of what the
    /// operator could check or uncheck, and must keep suppressing that default regardless of this filter.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> ApplySessionSelection(
        IReadOnlyList<McpServerConfig> registry,
        IReadOnlySet<string>? enabledServerNames) =>
        enabledServerNames is null
            ? registry
            : [.. registry.Where(server => !server.Enabled || enabledServerNames.Contains(server.Name))];
}
