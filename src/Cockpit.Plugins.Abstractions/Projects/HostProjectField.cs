namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One of the project editor's own fields (AC-604) — Name, Description, Logo, Behaviour, the MCP overlay, the
/// worktree switch — that a plugin can claim as externally managed through
/// <see cref="ICockpitHost.ClaimProjectOwnership"/>. Deliberately narrower than every field the editor draws:
/// the folder and the profile stay machine-local by nature (a checkout path, a credential-backed identity), so
/// they are not offered here.
/// </summary>
public enum HostProjectField
{
    Name,
    Description,
    Logo,
    Behavior,
    McpOverlay,
    WorktreeSwitch,
}
