using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a <see cref="ProjectMcpOverlay"/> inside a <see cref="ProjectEntry"/>. Reuses <see cref="McpServerEntry"/> so a project-owned server is written exactly like a registry one.</summary>
internal sealed class ProjectMcpOverlayEntry
{
    /// <summary>Null for a project that made no MCP choice — absent from the file rather than an empty list, which means "nothing is ticked".</summary>
    public List<string>? EnabledServerNames { get; set; }

    /// <summary>Only ever read: what an older build wrote in place of <see cref="EnabledServerNames"/> (see <see cref="ProjectMcpOverlay.DisabledServerNames"/>).</summary>
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
