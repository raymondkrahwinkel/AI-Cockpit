using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1514: a shared definition read through its source into the composition Core holds, for the callers with no dialog:
// the assistant's bind_shared_project (on the desktop and on a server) and a server's POST /projects/shared.
public static class SharedProjectBindings
{
    // The composition of `binding`, as the "Add to my projects…" dialog builds it.
    public static SharedProjectComposition Compose(string sharedProjectId, string sourceName, SharedProjectBinding binding) =>
        new(
            sharedProjectId,
            sourceName,
            binding.Checksum,
            binding.Name,
            binding.Description,
            binding.GitUrl,
            binding.BehaviorPrompt,
            binding.IsolateInWorktreeByDefault,
            binding.EnabledMcpServerNames,
            binding.LogoBytes,
            binding.Resources.Select(resource => new SharedProjectDefinitionResource(resource.Role, resource.Reference, resource.Label)));

    // Reads `sharedProjectId`'s definition through `source`; null plus the
    // source's own reason when it cannot be read.
    public static async Task<(SharedProjectComposition? Composition, string? Error)> ReadAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        CancellationToken cancellationToken)
    {
        var result = await source.PrepareBindingAsync(sharedProjectId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Binding is { } binding
            ? (Compose(sharedProjectId, source.SourceName, binding), null)
            : (null, result.Error is { Length: > 0 } error ? error : "Could not read this project's definition.");
    }

    // The dialog's "Choose…" route with no window, for the assistant: `resourceReferences` fills the
    // machine rows by position, and anything but one per row is refused with the rows spelled out.
    public static async Task<(SharedProjectBindResult? Result, string? Refusal)> ComposeAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken)
    {
        var (composition, error) = await ReadAsync(sharedProjectId, source, cancellationToken).ConfigureAwait(false);
        if (composition is null)
        {
            // Unreachable, signed out, or the project gone since it was listed — passed on rather than summarised:
            // "could not add it" tells the operator nothing they can act on.
            return (null, error);
        }

        // Positional, not keyed by label, since two rows can share a label. A blank row is silently dropped by the
        // dialog (fine when the operator sees the empty box); here nobody would, so it's refused (AC-246).
        var rows = composition.MachineRows;
        var given = resourceReferences?.Where(reference => !string.IsNullOrWhiteSpace(reference)).ToList() ?? [];
        if (given.Count != rows.Count)
        {
            return (null, rows.Count == 0
                ? "This project names no resources whose location is this machine's own, so there is nothing to pass in resources."
                : $"This project names {rows.Count} resource(s) whose location is this machine's own, and the shared definition does not carry them. "
                    + "Ask the operator for a local path for each, then call again with resources holding one entry per row, in this order: "
                    + string.Join("; ", rows.Select((row, index) => $"{index + 1}. {row.DisplayLabel}"))
                    + ".");
        }

        // The "Choose…" route, not the "Clone…" one: the folder was pointed at rather than cloned, so no GitUrl.
        return (new SharedProjectBindResult(
            composition.ToProject(
                sourceDirectory,
                gitUrl: null,
                profileLabel,
                composition.BehaviorPrompt,
                rows.Select((row, index) => new ProjectResource(given[index].Trim(), row.Role) { Label = row.Label })),
            composition.Checksum), null);
    }
}
