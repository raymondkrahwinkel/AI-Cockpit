using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a <see cref="ProjectMcpOverlay"/> inside a <see cref="ProjectEntry"/>. Reuses <see cref="McpServerEntry"/> so a project-owned server is written exactly like a registry one.</summary>
internal sealed class ProjectMcpOverlayEntry
{
    public List<string> DisabledServerNames { get; set; } = [];

    public List<McpServerEntry> AdditionalServers { get; set; } = [];

    public static ProjectMcpOverlayEntry? FromDomain(ProjectMcpOverlay overlay) => overlay.IsEmpty
        ? null
        : new ProjectMcpOverlayEntry
        {
            DisabledServerNames = [.. overlay.DisabledServerNames],
            AdditionalServers = [.. overlay.AdditionalServers.Select(McpServerEntry.FromDomain)],
        };

    public ProjectMcpOverlay ToDomain() => new()
    {
        DisabledServerNames = [.. DisabledServerNames],
        AdditionalServers = [.. AdditionalServers.Select(entry => entry.ToDomain())],
    };
}
