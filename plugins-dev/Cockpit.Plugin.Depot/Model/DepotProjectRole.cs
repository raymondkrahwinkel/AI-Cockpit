namespace Cockpit.Plugin.Depot.Model;

/// <summary>
/// A membership role Depot's own <c>list_projects</c> reports for one project (AC-245), parsed defensively rather
/// than shown as whatever raw text the server sent. <see cref="Unknown"/> is ordinal 0 — the least powerful
/// reading, not the most — so a role this build does not recognise (a server ahead of it, a field renamed) never
/// reads as anything more permissive than "cannot tell", the same discipline an on-disk enum in this codebase
/// already follows (see <c>ProjectResourceEntry</c>'s own remarks). Not persisted anywhere today — this only
/// normalizes a value read live and shown next to a shared project's row — but the same rule applies the day
/// something (AC-247's write gating) starts deciding on it.
/// </summary>
internal enum DepotProjectRole
{
    Unknown = 0,
    Viewer,
    Editor,
    Owner,
}

internal static class DepotProjectRoleParser
{
    public static DepotProjectRole Parse(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "viewer" => DepotProjectRole.Viewer,
        "editor" => DepotProjectRole.Editor,
        "owner" => DepotProjectRole.Owner,
        _ => DepotProjectRole.Unknown,
    };

    /// <summary>What a shared-project row shows for this role, or null for <see cref="DepotProjectRole.Unknown"/> — no pill rather than guessing.</summary>
    public static string? ToDisplayString(this DepotProjectRole role) => role switch
    {
        DepotProjectRole.Viewer => "Viewer",
        DepotProjectRole.Editor => "Editor",
        DepotProjectRole.Owner => "Owner",
        _ => null,
    };
}
