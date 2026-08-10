using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Why a write failed, beyond the generic `PluginMcpToolCallOutcome.Failed` outcome — the distinction a
// conflict window (AC-245/AC-246) needs to know whether to open at all (AC-247). Depot's own MCP tools carry no
// separate, typed error for either case — only the two error messages classified below (see
// `CockpitProjectDefinitionWriteResult.Failed`'s remarks on how) — so this is necessarily a
// best-effort read of Depot's wording, not a contract Depot promises to keep.
public enum CockpitProjectDefinitionWriteFailureKind
{
    // Depot failed the write for a reason this store cannot further classify — show `CockpitProjectDefinitionWriteResult.Error` as-is; never assume it was a conflict.
    Unclassified,

    // The `baseChecksum` this write sent no longer matches Depot's on-disk copy — someone else wrote first.
    // Re-read and offer a merge/overwrite/cancel choice; never retry with the same `baseChecksum`, and never
    // silently overwrite.
    ChecksumConflict,

    // Depot rejected the write because the caller's project role is below Editor, or the caller is not a project member at all — never retry; show `CockpitProjectDefinitionWriteResult.Error`, the named reason, instead of a silent refusal.
    PermissionDenied,
}

// What came of writing `.cockpit/project.json` to a Depot project (AC-244, conflict/permission classification AC-247).
// `DroppedExtensionKeys`: ExtensionData keys CockpitProjectDefinitionExtensionDataGuard refused on a Success write
// (AC-607 decision 3) — a secret-shaped, not-already-encrypted field a newer build wrote that this one would not
// forward. Empty on every other outcome and on a Success that dropped nothing.
public sealed record CockpitProjectDefinitionWriteResult(
    PluginMcpToolCallOutcome Outcome,
    string? Checksum,
    string? Error,
    CockpitProjectDefinitionWriteFailureKind FailureKind,
    IReadOnlyList<string>? DroppedExtensionKeys = null)
{
    public static CockpitProjectDefinitionWriteResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null, CockpitProjectDefinitionWriteFailureKind.Unclassified);

    public static CockpitProjectDefinitionWriteResult Success(string checksum, IReadOnlyList<string>? droppedExtensionKeys = null) =>
        new(PluginMcpToolCallOutcome.Success, checksum, null, CockpitProjectDefinitionWriteFailureKind.Unclassified, droppedExtensionKeys);

    // A failed write, classified from Depot's own error text — measured live against a real Depot server (AC-247),
    // not guessed, and cross-checked against Depot's own source (a project this repo does not own or ship, read
    // only to confirm the wire text):
    // - a `baseChecksum` mismatch: `WriteFileCommandHandler` phrases it
    // `"'{path}' changed since it was read; current checksum is {checksum}. Re-read and retry."` — matched here
    // on the fixed middle fragment, since path and checksum vary per call.
    // - a role below Editor: `ProjectMemberAccessGuard` phrases it
    // `"This action requires the Editor role on project '{project}'."`; a non-member gets
    // `"You are not a member of project '{project}'."` — both mean "no permission to write here", not
    // "something broke".
    // Depot's own MCP tool wrapping (its `_Unwrap` throws the failure's message, and the MCP layer carries only
    // that text back, never a machine-readable code) means this is inherently a text match against Depot's current
    // wording, not a typed signal — if Depot ever rephrases either message, this falls back to
    // `CockpitProjectDefinitionWriteFailureKind.Unclassified` rather than misclassifying, since neither
    // fragment would match.
    public static CockpitProjectDefinitionWriteResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, error, _ClassifyFailure(error));

    // A write this store refused before ever calling Depot, because a caller-supplied `CockpitProjectRole` already can't write — see `CockpitProjectDefinitionStore.WriteAsync`'s `callerRole` parameter.
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
