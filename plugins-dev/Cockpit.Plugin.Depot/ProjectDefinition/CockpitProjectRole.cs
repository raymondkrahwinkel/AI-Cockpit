namespace Cockpit.Plugin.Depot.ProjectDefinition;

// A member's role on a Depot project — mirrors Depot's own `ProjectRole` enum (Depot source:
// `Depot.Domain/Enums/ProjectRole.cs`) value-for-value, including the gapped numbering: Depot's own doc
// comment there explains the gap exists so a rank comparison, never declaration order, decides "at least Editor".
// Kept as a plain mirror rather than referencing Depot's assembly — this plugin has no compile-time dependency on
// Depot's server code, only on the wire text and JSON shapes its MCP tools return (AC-244/AC-247).
public enum CockpitProjectRole
{
    Viewer = -100,
    Editor = 0,
    Owner = 100,
}

// Where a `CockpitProjectRole` comes from (Depot's own `list_projects` tool returns a `role`
// string per project) and what it authorizes — the write side's permission vocabulary (AC-247). Depot enforces the
// real rule at its own MCP layer (see `CockpitProjectDefinitionWriteFailureKind.PermissionDenied`);
// these helpers exist so a caller that already knows the role — e.g. a project editor holding the row from a prior
// `list_projects` call — can name the reason and skip the round trip instead of discovering it from a failed
// write. The UI that dims fields on this is not built here (AC-246); this is the data it leans on.
public static class CockpitProjectRoles
{
    // Parses the `role` string Depot's `list_projects` tool returns for one project (case-insensitive).
    // Unrecognized or missing text parses to `null` — never a guessed default, so an unmapped future role name
    // fails open to "unknown" rather than silently landing on Viewer or Editor.
    public static CockpitProjectRole? TryParse(string? role) => role?.Trim() switch
    {
        { Length: 0 } => null,
        var value when string.Equals(value, nameof(CockpitProjectRole.Viewer), StringComparison.OrdinalIgnoreCase) => CockpitProjectRole.Viewer,
        var value when string.Equals(value, nameof(CockpitProjectRole.Editor), StringComparison.OrdinalIgnoreCase) => CockpitProjectRole.Editor,
        var value when string.Equals(value, nameof(CockpitProjectRole.Owner), StringComparison.OrdinalIgnoreCase) => CockpitProjectRole.Owner,
        _ => null,
    };

    // Whether this role may write `CockpitProjectDefinitionStore.WriteAsync`'s target file — Editor and above, mirroring the `ProjectRole.Editor` minimum Depot's own `write` tool requires.
    public static bool CanWrite(this CockpitProjectRole role) => role >= CockpitProjectRole.Editor;

    // The named reason to show instead of a silent refusal when `CanWrite` is false — never blank.
    public static string WriteDeniedReason(this CockpitProjectRole role) =>
        $"You are a {role} on this project. Shared fields are read-only; ask an Owner for Editor access.";
}
