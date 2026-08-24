namespace Cockpit.Core.Plugins;

// AC-1013: One workflow template a store offers (#69) — a flow as text, so unlike a plugin there is
// no assembly/code/hash to pin, just identity + description + checksum. Not inert though: a flow can
// carry a shell command, so an installed template is never armed until the operator reads and arms it.
public sealed record WorkflowTemplateStoreEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    string? Version,
    string Path,
    string? Sha256 = null,
    string? Category = null,
    IReadOnlyList<string>? Requires = null);
