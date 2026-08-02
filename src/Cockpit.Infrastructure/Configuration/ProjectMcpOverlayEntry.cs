using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `ProjectMcpOverlay` inside a `ProjectEntry`. Reuses `McpServerEntry` so a project-owned server is written exactly like a registry one.
internal sealed class ProjectMcpOverlayEntry
{
    // Null for a project that made no MCP choice — absent from the file rather than an empty list, which means "nothing is ticked".
    public List<string>? EnabledServerNames { get; set; }

    // Only ever read: what an older build wrote in place of `EnabledServerNames` (see `ProjectMcpOverlay.DisabledServerNames`).
    public List<string> DisabledServerNames { get; set; } = [];

    public List<McpServerEntry> AdditionalServers { get; set; } = [];

    public static ProjectMcpOverlayEntry? FromDomain(ProjectMcpOverlay overlay) => overlay.IsEmpty
        ? null
        : new ProjectMcpOverlayEntry
        {
            EnabledServerNames = overlay.EnabledServerNames is { } enabled ? [.. enabled] : null,
            DisabledServerNames = [.. overlay.DisabledServerNames],
            AdditionalServers = [.. overlay.AdditionalServers.Select(McpServerEntry.FromDomain)],
        };

    public ProjectMcpOverlay ToDomain() => new()
    {
        EnabledServerNames = EnabledServerNames is { } enabled ? [.. enabled] : null,
        DisabledServerNames = [.. DisabledServerNames],
        AdditionalServers = [.. AdditionalServers.Select(entry => entry.ToDomain())],
    };
}
