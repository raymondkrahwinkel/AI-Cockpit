namespace Cockpit.Core.Sessions.Permissions;

// How risky a tool call is, as far as a headless (delegated) session can tell from the MCP tool's own
// annotations. It is the axis the delegation permission ceiling grades against (AC-79): a read-only tool is
// safe to run unattended, a destructive one is not unless the ceiling explicitly says so, and a tool whose
// server offers no reliable hint is `Unknown` — trusted only when the operator listed it.
//
// `Unknown` is deliberately the zero value, so the default of an uninitialised or missing entry is
// the fail-safe (deny-unless-allow-listed) class rather than the most permissive one — a security enum must
// fail closed when read before it is set.
public enum ToolPermissionClass
{
    // The server gave no `readOnlyHint` at all, so the class cannot be told — or two enabled servers
    // disagree on it. Annotations are advisory and server-supplied, so an absent/ambiguous one is not read as
    // "safe": an unknown tool runs unattended only when the operator put it on the profile's allow-list. The zero
    // value, so a default/missing lookup is deny-by-default.
    Unknown = 0,

    // The server declares the tool read-only (`readOnlyHint = true`): it observes, it does not change anything.
    ReadOnly,

    // The tool changes state but the server declares it non-destructive (`readOnlyHint = false`, `destructiveHint = false`).
    Write,

    // The tool changes state and is destructive, or its destructiveness is unstated for a non-read-only tool
    // (`readOnlyHint = false` with `destructiveHint` true or absent) — treated as destructive because
    // the MCP spec's own default for a non-read-only tool is destructive, and the safe reading of silence is the
    // worse case.
    Destructive,
}
