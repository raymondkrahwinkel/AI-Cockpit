namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// How risky a tool call is, as far as a headless (delegated) session can tell from the MCP tool's own
/// annotations. It is the axis the delegation permission ceiling grades against (AC-79): a read-only tool is
/// safe to run unattended, a destructive one is not unless the ceiling explicitly says so, and a tool whose
/// server offers no reliable hint is <see cref="Unknown"/> — trusted only when the operator listed it.
/// </summary>
public enum ToolPermissionClass
{
    /// <summary>The server declares the tool read-only (<c>readOnlyHint = true</c>): it observes, it does not change anything.</summary>
    ReadOnly,

    /// <summary>The tool changes state but the server declares it non-destructive (<c>readOnlyHint = false</c>, <c>destructiveHint = false</c>).</summary>
    Write,

    /// <summary>
    /// The tool changes state and is destructive, or its destructiveness is unstated for a non-read-only tool
    /// (<c>readOnlyHint = false</c> with <c>destructiveHint</c> true or absent) — treated as destructive because
    /// the MCP spec's own default for a non-read-only tool is destructive, and the safe reading of silence is the
    /// worse case.
    /// </summary>
    Destructive,

    /// <summary>
    /// The server gave no <c>readOnlyHint</c> at all, so the class cannot be told. Annotations are advisory and
    /// server-supplied, so an absent one is not read as "safe": an unknown tool runs unattended only when the
    /// operator put it on the profile's allow-list.
    /// </summary>
    Unknown,
}
