using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// Why a write failed, beyond the generic <see cref="PluginMcpToolCallOutcome.Failed"/> outcome — the distinction a
/// conflict window (AC-245/AC-246) needs to know whether to open at all (AC-247). Depot's own MCP tools carry no
/// separate, typed error for either case — only the two error messages classified below (see
/// <see cref="CockpitProjectDefinitionWriteResult.Failed"/>'s remarks on how) — so this is necessarily a
/// best-effort read of Depot's wording, not a contract Depot promises to keep.
/// </summary>
public enum CockpitProjectDefinitionWriteFailureKind
{
    /// <summary>Depot failed the write for a reason this store cannot further classify — show <see cref="CockpitProjectDefinitionWriteResult.Error"/> as-is; never assume it was a conflict.</summary>
    Unclassified,

    /// <summary>
    /// The <c>baseChecksum</c> this write sent no longer matches Depot's on-disk copy — someone else wrote first.
    /// Re-read and offer a merge/overwrite/cancel choice; never retry with the same <c>baseChecksum</c>, and never
    /// silently overwrite.
    /// </summary>
    ChecksumConflict,

    /// <summary>Depot rejected the write because the caller's project role is below Editor, or the caller is not a project member at all — never retry; show <see cref="CockpitProjectDefinitionWriteResult.Error"/>, the named reason, instead of a silent refusal.</summary>
    PermissionDenied,
}

/// <summary>What came of writing <c>.cockpit/project.json</c> to a Depot project (AC-244, conflict/permission classification AC-247).</summary>
public sealed record CockpitProjectDefinitionWriteResult(
    PluginMcpToolCallOutcome Outcome, string? Checksum, string? Error, CockpitProjectDefinitionWriteFailureKind FailureKind)
{
    public static CockpitProjectDefinitionWriteResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null, CockpitProjectDefinitionWriteFailureKind.Unclassified);

    public static CockpitProjectDefinitionWriteResult Success(string checksum) =>
        new(PluginMcpToolCallOutcome.Success, checksum, null, CockpitProjectDefinitionWriteFailureKind.Unclassified);

    /// <summary>
    /// A failed write, classified from Depot's own error text — measured live against a real Depot server (AC-247),
    /// not guessed, and cross-checked against Depot's own source (a project this repo does not own or ship, read
    /// only to confirm the wire text):
    /// <list type="bullet">
    /// <item>a <c>baseChecksum</c> mismatch: <c>WriteFileCommandHandler</c> phrases it
    /// <c>"'{path}' changed since it was read; current checksum is {checksum}. Re-read and retry."</c> — matched here
    /// on the fixed middle fragment, since path and checksum vary per call.</item>
    /// <item>a role below Editor: <c>ProjectMemberAccessGuard</c> phrases it
    /// <c>"This action requires the Editor role on project '{project}'."</c>; a non-member gets
    /// <c>"You are not a member of project '{project}'."</c> — both mean "no permission to write here", not
    /// "something broke".</item>
    /// </list>
    /// Depot's own MCP tool wrapping (its <c>_Unwrap</c> throws the failure's message, and the MCP layer carries only
    /// that text back, never a machine-readable code) means this is inherently a text match against Depot's current
    /// wording, not a typed signal — if Depot ever rephrases either message, this falls back to
    /// <see cref="CockpitProjectDefinitionWriteFailureKind.Unclassified"/> rather than misclassifying, since neither
    /// fragment would match.
    /// </summary>
    public static CockpitProjectDefinitionWriteResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, error, _ClassifyFailure(error));

    /// <summary>A write this store refused before ever calling Depot, because a caller-supplied <see cref="CockpitProjectRole"/> already can't write — see <see cref="CockpitProjectDefinitionStore.WriteAsync"/>'s <c>callerRole</c> parameter.</summary>
    public static CockpitProjectDefinitionWriteResult PermissionDenied(string reason) =>
        new(PluginMcpToolCallOutcome.Failed, null, reason, CockpitProjectDefinitionWriteFailureKind.PermissionDenied);

    private static CockpitProjectDefinitionWriteFailureKind _ClassifyFailure(string error)
    {
        if (error.Contains("changed since it was read; current checksum is", StringComparison.Ordinal))
            return CockpitProjectDefinitionWriteFailureKind.ChecksumConflict;

        if (error.Contains("requires the Editor role on project", StringComparison.Ordinal)
            || error.Contains("are not a member of project", StringComparison.Ordinal))
            return CockpitProjectDefinitionWriteFailureKind.PermissionDenied;

        return CockpitProjectDefinitionWriteFailureKind.Unclassified;
    }
}
