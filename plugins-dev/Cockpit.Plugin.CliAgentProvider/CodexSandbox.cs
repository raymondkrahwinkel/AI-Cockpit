namespace Cockpit.Plugin.CliAgentProvider;

// The Codex sandbox modes the cockpit offers, in one place so the launch options (SDK and TTY) and the live
// sandbox control stay in sync. The launch paths pass the kebab value straight through as the app-server's
// SandboxMode string on `thread/start`; the live per-turn override (#45 D4 inc2b) instead needs the
// SandboxPolicy object's camelCase `type` discriminator, which `ToPolicyType` maps the same
// kebab choice onto — so the operator sees one vocabulary while the wire gets the form each path requires.
internal static class CodexSandbox
{
    public static IReadOnlyList<string> Choices { get; } = ["read-only", "workspace-write", "danger-full-access"];

    // Maps a chosen kebab sandbox mode to the SandboxPolicy `type` discriminator, or `null` for an unknown one.
    public static string? ToPolicyType(string? mode) => mode switch
    {
        "read-only" => "readOnly",
        "workspace-write" => "workspaceWrite",
        "danger-full-access" => "dangerFullAccess",
        _ => null,
    };

    // The least-privilege sandbox for a delegated session's permission ceiling (AC-112): a ceiling that allows
    // edits maps to `workspace-write` (writable, but bounded to the working directory); plan/default/unknown
    // map to `null` so the caller keeps the profile's configured default (Codex's own read-only).
    // Never returns `danger-full-access` — full disk access stays an explicit operator choice, never derived.
    // This is what lets a delegated Codex task write instead of stalling at read-only on an approval nobody can
    // answer, while staying bounded by the same ceiling the host already set for the session.
    public static string? ForCeiling(string? permissionMode) => permissionMode switch
    {
        "acceptEdits" or "bypassPermissions" => "workspace-write",
        _ => null,
    };
}
