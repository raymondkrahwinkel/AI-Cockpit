namespace Cockpit.Core.Sessions.Permissions;

// AC-79: how risky a tool call is, as far as a headless (delegated) session can tell from the MCP tool's own
// annotations — the axis the delegation permission ceiling grades against. `Unknown` is deliberately the
// zero value, so an uninitialised/missing entry defaults to fail-closed (deny-unless-allow-listed).
public enum ToolPermissionClass
{
    // No `readOnlyHint` at all, or two enabled servers disagree — an absent/ambiguous annotation is never
    // read as "safe": an unknown tool runs unattended only when the operator allow-listed it.
    Unknown = 0,

    // The server declares the tool read-only (`readOnlyHint = true`): it observes, it does not change anything.
    ReadOnly,

    // The tool changes state but the server declares it non-destructive (`readOnlyHint = false`, `destructiveHint = false`).
    Write,

    // Destructive, or unstated for a non-read-only tool — treated as destructive per the MCP spec's own
    // default, the safe reading of silence.
    Destructive,
}
