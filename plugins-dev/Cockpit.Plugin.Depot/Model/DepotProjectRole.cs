namespace Cockpit.Plugin.Depot.Model;

// A membership role Depot's own `list_projects` reports for one project (AC-245), parsed defensively rather
// than shown as whatever raw text the server sent. `Unknown` is ordinal 0 — the least powerful
// reading, not the most — so a role this build does not recognise (a server ahead of it, a field renamed) never
// reads as anything more permissive than "cannot tell", the same discipline an on-disk enum in this codebase
// already follows (see `ProjectResourceEntry`'s own remarks). Not persisted anywhere today — this only
// normalizes a value read live and shown next to a shared project's row — but the same rule applies the day
// something (AC-247's write gating) starts deciding on it.
internal enum DepotProjectRole
{
    Unknown = 0,
    Viewer,
    Editor,
    Owner,

    // AC-699: not a membership role at all — Depot reports it for *every* project when the caller is a global
    // admin (`ListProjectsForUserQuery`), instead of whatever membership row they happen to also hold. Above
    // Owner, so the ordering this enum is read by (`CanWrite`) keeps meaning "least powerful first".
    Admin,
}

internal static class DepotProjectRoleParser
{
    public static DepotProjectRole Parse(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "viewer" => DepotProjectRole.Viewer,
        "editor" => DepotProjectRole.Editor,
        "owner" => DepotProjectRole.Owner,
        "admin" => DepotProjectRole.Admin,
        _ => DepotProjectRole.Unknown,
    };

    // Whether this role may write a project's definition — the one question both the publish picker and AC-247's
    // write-back gating ask. A comparison against the ordered enum rather than a list of roles each caller
    // repeats: AC-699 was exactly that list going stale when Depot started reporting a role nobody enumerated.
    public static bool CanWrite(this DepotProjectRole role) => role >= DepotProjectRole.Editor;

    // What a shared-project row shows for this role, or null for `DepotProjectRole.Unknown` — no pill rather than guessing.
    public static string? ToDisplayString(this DepotProjectRole role) => role switch
    {
        DepotProjectRole.Viewer => "Viewer",
        DepotProjectRole.Editor => "Editor",
        DepotProjectRole.Owner => "Owner",
        DepotProjectRole.Admin => "Admin",
        _ => null,
    };
}
