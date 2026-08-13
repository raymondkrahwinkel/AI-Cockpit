using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

// One Depot connection's own `ISharedProjectSource` (AC-245): lists this connection's projects through
// `list_projects` (the same call `DepotMemorySource` already makes for its picker), then reads
// each one's `.cockpit/project.json` (`CockpitProjectDefinitionStore.ReadAsync`, AC-244) to learn
// its portable name and description — a Depot project without one is not offered here at all: not every project on
// a connection has opted into being shared this way.
//
// ponytail: one MCP round trip per listed project on top of the initial `list_projects` call, every time the
// Projects workspace loads — no caching. Acceptable for the handful of projects a connection realistically carries
// today; batch or cache here first if a connection with hundreds of shared projects makes this the slow part.
internal sealed class DepotSharedProjectSource(
    DepotConnectionRegistration connection, string scheme, ICockpitHost host, HttpClient? httpClient = null)
    : ISharedProjectSource
{
    public string Key => scheme;

    public string SourceName => $"Depot — {connection.Name}";

    public async Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken)
    {
        var listResult = await host.CallMcpToolAsync(
            connection.McpServerName,
            "list_projects",
            new Dictionary<string, object?> { ["includeSummary"] = false },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        switch (listResult.Outcome)
        {
            case PluginMcpToolCallOutcome.AuthorizationRequired:
                return SharedProjectListResult.Failed("Sign in to this Depot connection to see its shared projects.");
            case PluginMcpToolCallOutcome.Success:
                break;
            default:
                return SharedProjectListResult.Failed(
                    listResult.Error is { Length: > 0 } error ? error : "Depot did not return a list of projects.");
        }

        if (!DepotMemorySource.TryParseProjects(listResult.Content ?? string.Empty, out var listed, out var parseError))
        {
            return SharedProjectListResult.Failed(parseError ?? "Depot's project list came back in an unexpected shape.");
        }

        var shared = new List<SharedProject>(listed.Count);
        var unreadable = new List<UnreadableSharedProject>();
        foreach (var project in listed)
        {
            // DEP-159's list_projects mixes "Project" and "Brain" kinds together; a Brain is not something a
            // Cockpit project ever binds to.
            if (string.Equals(project.Kind, "Brain", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var role = DepotProjectRoleParser.Parse(project.Role);

            // Raymond, 2026-08-02: intended end state is Depot itself changing so a Viewer gets MCP read rights
            // (write stays Editor+) — a DEP-side fix, not a Cockpit one, and not shipped yet. Until it lands,
            // Depot's own access guard (measured against origin/dev: ReadFileQuery.cs requires ProjectRole.Editor)
            // makes every Viewer's read of .cockpit/project.json fail regardless of whether that project actually
            // carries one. The read is attempted for every role alike — once the DEP fix ships, a Viewer's read
            // simply starts succeeding here with no Cockpit-side change required — but a Viewer/Unknown-role
            // failure is reported as a named, visible-but-unreadable outcome instead of silently dropped, since
            // today it is ambiguous whether that project even opted into Cockpit sharing. An Editor/Owner failure
            // still means, unambiguously, "not shared this way" and is left out as before.
            var definitionResult = await CockpitProjectDefinitionStore.ReadAsync(
                host, connection.McpServerName, project.Slug, cancellationToken).ConfigureAwait(false);

            if (definitionResult.Outcome != PluginMcpToolCallOutcome.Success || definitionResult.Definition is not { } definition)
            {
                if (!role.CanWrite())
                {
                    unreadable.Add(new UnreadableSharedProject(
                        $"{scheme}:{project.Slug}", project.Name is { Length: > 0 } ? project.Name : project.Slug, role.ToDisplayString()));
                }

                // Otherwise: no .cockpit/project.json at all (the ordinary case for a project that never opted into
                // Cockpit sharing) or a definition this build cannot parse — leave this one project out. One bad or
                // unshared project must never cost every other project on this connection.
                continue;
            }

            var name = definition.Name is { Length: > 0 }
                ? definition.Name
                : project.Name is { Length: > 0 } ? project.Name : project.Slug;

            shared.Add(new SharedProject($"{scheme}:{project.Slug}", name)
            {
                Description = definition.Description,
                Role = role.ToDisplayString(),
                // AC-247: Editor/Owner may write WriteBackAsync's target; Viewer/Unknown may not — the same
                // minimum DepotProjectRoleParser's own remarks already earmarked this enum for.
                CanWriteBack = role.CanWrite(),
            });
        }

        return SharedProjectListResult.Success(shared) with { VisibleButUnreadable = unreadable };
    }

    // Reads `.cockpit/project.json` a second time (AC-246), for the one project the operator is binding right
    // now rather than every project on this connection — `ListAsync`'s own read only ever kept
    // `CockpitProjectDefinition.Name`/`Description`, so a bind step needs its own call for the
    // rest (`GitUrl`, `BehaviorPrompt`, the worktree switch, the MCP overlay, the resource rows).
    // `id` is expected in this source's own shape (`"{scheme}:{slug}"`), so parsing it back is a
    // prefix check against `id`'s own scheme rather than a general `ProjectMemoryRef`-style
    // parse — this plugin cannot reference `Cockpit.Core` (see this class's own remarks on
    // `ProjectResourcePortabilityClassifier`), and it does not need to: it only ever has to recognise
    // its own scheme, never anyone else's.
    public async Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken)
    {
        var prefix = $"{scheme}:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length)
        {
            return SharedProjectBindingResult.Failed($"'{id}' does not belong to this Depot connection.");
        }

        var slug = id[prefix.Length..];
        var definitionResult = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection.McpServerName, slug, cancellationToken).ConfigureAwait(false);

        if (definitionResult.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return SharedProjectBindingResult.Failed("Sign in to this Depot connection to finish setting up this project.");
        }

        if (definitionResult.Outcome != PluginMcpToolCallOutcome.Success || definitionResult.Definition is not { } definition)
        {
            return SharedProjectBindingResult.Failed(
                definitionResult.Error is { Length: > 0 } error ? error : "Depot did not return a project definition.");
        }

        var binding = _ToBinding(slug, definition, definitionResult.Checksum);

        // AC-763: a bind is the moment a shared logo first has to become visible on this machine, without the
        // operator picking anything — so it is worth the extra round trip here, unlike WriteBackAsync's own
        // conflict snapshot (_ToBinding's other caller), which never shows a logo and would only pay for one.
        if (definition.Logo is { Length: > 0 })
        {
            var download = await CockpitProjectLogoBlob.DownloadAsync(host, connection.McpServerName, slug, httpClient, cancellationToken)
                .ConfigureAwait(false);
            if (download.Outcome == PluginMcpToolCallOutcome.Success)
            {
                binding = binding with { LogoBytes = download.Bytes };
            }

            // A failed download costs the picture, not the bind — SharedProjectBinding.LogoBytes' own remarks.
        }

        return SharedProjectBindingResult.Success(binding);
    }

    // AC-247. Re-reads id's current definition first — not for its own checksum (the caller's baseChecksum, from
    // the read the operator's edit actually started from, is what CockpitProjectDefinitionStore.WriteAsync is
    // asked to defend), but so GitUrl, Resources and Logo — every field SharedProjectDefinitionEdit does not
    // mention — carry through byte-for-byte rather than being reconstructed from SharedProjectBinding's own lossy
    // read shape (a resource row's Placeholder flag, in particular, does not survive a round trip through
    // SharedProjectBindingResource — rebuilding one from just Role/Reference/Label would silently drop every
    // placeholder row on write).
    //
    // This fresh read cannot substitute for baseChecksum: if nothing changed since the operator opened the editor,
    // it is (by definition) identical to what that earlier read saw, so reusing it for pass-through fields changes
    // nothing a conflict would have caught. If something did change, the write below still carries the operator's
    // own, older baseChecksum — so the server-side check catches the change regardless of which fields it touched,
    // exactly the guarantee optimistic concurrency exists for.
    public async Task<SharedProjectWriteBackResult> WriteBackAsync(
        string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken)
    {
        var prefix = $"{scheme}:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length)
        {
            return SharedProjectWriteBackResult.Failed($"'{id}' does not belong to this Depot connection.");
        }

        var slug = id[prefix.Length..];
        var currentRead = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection.McpServerName, slug, cancellationToken).ConfigureAwait(false);

        if (currentRead.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return SharedProjectWriteBackResult.Failed("Sign in to this Depot connection to save this project.");
        }

        if (currentRead.Outcome != PluginMcpToolCallOutcome.Success || currentRead.Definition is not { } current)
        {
            return SharedProjectWriteBackResult.Failed(
                currentRead.Error is { Length: > 0 } error ? error : "Depot did not return a project definition.");
        }

        // AC-763: the blob move happens before the checksum-guarded write below, so a failure here returns
        // immediately rather than leave project.json pointing at a logo that never actually landed. A blob
        // orphaned by a lost checksum race is harmless — the next save re-reads `current` and retries it.
        string? logoPath;
        switch (edit.LogoEdit)
        {
            case null:
                logoPath = current.Logo;
                break;

            case { PngBytes: { Length: > 0 } pngBytes }:
                var upload = await CockpitProjectLogoBlob.UploadAsync(host, connection.McpServerName, slug, pngBytes, httpClient, cancellationToken)
                    .ConfigureAwait(false);
                if (upload.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
                {
                    return SharedProjectWriteBackResult.PermissionDenied("Sign in to this Depot connection to save this project's logo.");
                }

                if (upload.Outcome != PluginMcpToolCallOutcome.Success)
                {
                    return SharedProjectWriteBackResult.Failed(upload.Error is { Length: > 0 } error ? error : "Could not save this project's logo.");
                }

                logoPath = CockpitProjectLogoBlob.BlobPath;
                break;

            default: // Cleared, or Replace called with no bytes — either way, nothing to keep.
                if (current.Logo is { Length: > 0 })
                {
                    var delete = await CockpitProjectLogoBlob.DeleteAsync(host, connection.McpServerName, slug, cancellationToken).ConfigureAwait(false);
                    if (delete.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
                    {
                        return SharedProjectWriteBackResult.PermissionDenied("Sign in to this Depot connection to save this project's logo.");
                    }

                    if (delete.Outcome != PluginMcpToolCallOutcome.Success)
                    {
                        return SharedProjectWriteBackResult.Failed(delete.Error is { Length: > 0 } error ? error : "Could not remove this project's logo.");
                    }
                }

                logoPath = null;
                break;
        }

        var merged = new CockpitProjectDefinition
        {
            Name = edit.Name,
            Description = edit.Description,
            GitUrl = current.GitUrl,
            BehaviorPrompt = edit.BehaviorPrompt,
            IsolateInWorktreeByDefault = edit.IsolateInWorktreeByDefault,
            // Adversarial review finding: this used to fall back to `current.McpOverlay` on null, reading
            // SharedProjectDefinitionEdit's own "null means no opinion, every server ticked" (the same idiom
            // SharedProjectBinding.EnabledMcpServerNames already documents for the read direction) as "the operator
            // didn't touch this." The two mean opposite things — an operator who re-ticks every server to clear a
            // remote restriction sends null on purpose, and the old code silently kept the restriction Depot
            // already had. Always reflect what edit actually says, never fall back to what was already there.
            McpOverlay = edit.EnabledMcpServerNames is { } enabled ? new CockpitProjectMcpOverlayEntry { Enabled = [.. enabled] } : null,
            Resources = current.Resources,
            Logo = logoPath,
        };

        var writeResult = await CockpitProjectDefinitionStore.WriteAsync(
            host, connection.McpServerName, slug, merged, baseChecksum, callerRole: null, cancellationToken).ConfigureAwait(false);

        return writeResult.Outcome switch
        {
            PluginMcpToolCallOutcome.Success => SharedProjectWriteBackResult.Success(writeResult.Checksum!),
            _ when writeResult.FailureKind == CockpitProjectDefinitionWriteFailureKind.ChecksumConflict =>
                // `current`/`currentRead.Checksum` — the read this call itself did, moments before the rejected
                // write — is exactly "what Depot has now" a conflict view needs; no second read to show it.
                SharedProjectWriteBackResult.Conflict(_ToBinding(slug, current, currentRead.Checksum)),
            _ when writeResult.FailureKind == CockpitProjectDefinitionWriteFailureKind.PermissionDenied =>
                SharedProjectWriteBackResult.PermissionDenied(
                    writeResult.Error is { Length: > 0 } error ? error : "You do not have permission to write here."),
            _ => SharedProjectWriteBackResult.Failed(
                writeResult.Error is { Length: > 0 } error ? error : "Depot did not confirm the write."),
        };
    }

    public bool CanPublish => true;

    // AC-620: the same list_projects call ListAsync makes, but kept unfiltered by "does it already carry a
    // definition" — a publish target is a container to write the *first* definition into, so a project that has
    // never opted into Cockpit sharing is exactly the common case here, not the one ListAsync leaves out.
    public async Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken)
    {
        var listResult = await host.CallMcpToolAsync(
            connection.McpServerName,
            "list_projects",
            new Dictionary<string, object?> { ["includeSummary"] = false },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        switch (listResult.Outcome)
        {
            case PluginMcpToolCallOutcome.AuthorizationRequired:
                return SharedProjectPublishTargetListResult.Failed("Sign in to this Depot connection to publish a project.");
            case PluginMcpToolCallOutcome.Success:
                break;
            default:
                return SharedProjectPublishTargetListResult.Failed(
                    listResult.Error is { Length: > 0 } error ? error : "Depot did not return a list of projects.");
        }

        if (!DepotMemorySource.TryParseProjects(listResult.Content ?? string.Empty, out var listed, out var parseError))
        {
            return SharedProjectPublishTargetListResult.Failed(parseError ?? "Depot's project list came back in an unexpected shape.");
        }

        // AC-620's own decision 4: only a project the operator can already write to is offered — Depot has no
        // create_project call to fall back on, so a Viewer's row would dead-end the moment it is chosen. AC-699:
        // CanWrite answers that, not a role list repeated here — this one missed "Admin" and emptied the dropdown.
        var targets = listed
            .Where(project => !string.Equals(project.Kind, "Brain", StringComparison.OrdinalIgnoreCase))
            .Select(project => (project.Slug, project.Name, Role: DepotProjectRoleParser.Parse(project.Role)))
            .Where(entry => entry.Role.CanWrite())
            .Select(entry => new SharedProjectPublishTarget(
                $"{scheme}:{entry.Slug}",
                entry.Name is { Length: > 0 } ? entry.Name : entry.Slug,
                entry.Role.ToDisplayString()))
            .ToList();

        return SharedProjectPublishTargetListResult.Success(targets);
    }

    // AC-620. Reads first — Depot's own "[NotFound]" read wording (measured live against a real server) is what
    // tells "nothing there yet" from "already published" apart before writing with no baseChecksum.
    public async Task<SharedProjectPublishResult> PublishAsync(
        string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken)
    {
        var prefix = $"{scheme}:";
        if (!targetId.StartsWith(prefix, StringComparison.Ordinal) || targetId.Length <= prefix.Length)
        {
            return SharedProjectPublishResult.Failed($"'{targetId}' does not belong to this Depot connection.");
        }

        var slug = targetId[prefix.Length..];
        // ponytail: read-then-write, not atomic — two concurrent first publishes of the same target can both pass
        // this read before either writes, and the second silently overwrites the first. Depot's write tool has no
        // create-if-absent flag to close this; upgrade path is a DEP-ticket to add one.
        var existing = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection.McpServerName, slug, cancellationToken).ConfigureAwait(false);

        if (existing.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return SharedProjectPublishResult.Failed("Sign in to this Depot connection to publish this project.");
        }

        if (existing.Outcome == PluginMcpToolCallOutcome.Success)
        {
            return SharedProjectPublishResult.AlreadyPublished(
                "This Depot project already carries a shared definition — finish setting it up here instead of publishing over it.");
        }

        if (existing.Error is not { } existingError || !existingError.StartsWith("[NotFound]", StringComparison.Ordinal))
        {
            return SharedProjectPublishResult.Failed(
                existing.Error is { Length: > 0 } error ? error : "Couldn't confirm whether this Depot project is already published.");
        }

        // Never AdditionalInfo/secret material here by construction — SharedProjectPublishDefinition carries no
        // field one could populate (see its own remarks); CockpitProjectResourceFilter is what additionally drops a
        // secret-shaped resource reference (AC-612) before anything below reaches the wire.
        var filtered = CockpitProjectResourceFilter.Apply(
            definition.Resources.Select(resource => (resource.Role, resource.Reference, resource.Label)));

        // AC-763, same ordering reasoning as WriteBackAsync: upload before writing the definition that references
        // it, and fail the whole publish rather than leave a project.json that names a logo that never landed.
        string? logoPath = null;
        if (definition.LogoBytes is { Length: > 0 } logoBytes)
        {
            var upload = await CockpitProjectLogoBlob.UploadAsync(host, connection.McpServerName, slug, logoBytes, httpClient, cancellationToken)
                .ConfigureAwait(false);
            if (upload.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
            {
                return SharedProjectPublishResult.Failed("Sign in to this Depot connection to publish this project's logo.");
            }

            if (upload.Outcome != PluginMcpToolCallOutcome.Success)
            {
                return SharedProjectPublishResult.Failed(upload.Error is { Length: > 0 } error ? error : "Could not publish this project's logo.");
            }

            logoPath = CockpitProjectLogoBlob.BlobPath;
        }

        var toWrite = new CockpitProjectDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            GitUrl = definition.GitUrl,
            BehaviorPrompt = definition.BehaviorPrompt,
            IsolateInWorktreeByDefault = definition.IsolateInWorktreeByDefault,
            McpOverlay = definition.EnabledMcpServerNames is { } enabled ? new CockpitProjectMcpOverlayEntry { Enabled = [.. enabled] } : null,
            Resources = filtered.Portable.Count == 0 ? null : [.. filtered.Portable],
            Logo = logoPath,
        };

        var writeResult = await CockpitProjectDefinitionStore.WriteAsync(
            host, connection.McpServerName, slug, toWrite, baseChecksum: null, callerRole: null, cancellationToken).ConfigureAwait(false);

        return writeResult.Outcome switch
        {
            PluginMcpToolCallOutcome.Success => SharedProjectPublishResult.Success($"{scheme}:{slug}"),
            _ when writeResult.FailureKind == CockpitProjectDefinitionWriteFailureKind.PermissionDenied =>
                SharedProjectPublishResult.PermissionDenied(
                    writeResult.Error is { Length: > 0 } error ? error : "You do not have permission to publish here."),
            _ => SharedProjectPublishResult.Failed(
                writeResult.Error is { Length: > 0 } error ? error : "Depot did not confirm the write."),
        };
    }

    // Shared by PrepareBindingAsync and WriteBackAsync's own conflict snapshot — both ever have to turn a
    // CockpitProjectDefinition into the plugin-shape-agnostic SharedProjectBinding the same way.
    private static SharedProjectBinding _ToBinding(string slug, CockpitProjectDefinition definition, string? checksum)
    {
        var name = definition.Name is { Length: > 0 } ? definition.Name : slug;

        return new SharedProjectBinding(name)
        {
            Description = definition.Description,
            GitUrl = definition.GitUrl,
            BehaviorPrompt = definition.BehaviorPrompt,
            IsolateInWorktreeByDefault = definition.IsolateInWorktreeByDefault,
            EnabledMcpServerNames = definition.McpOverlay?.Enabled,
            // AC-246 (Raymond, 2026-08-02): a Placeholder row's Reference is blank on purpose — that is the row
            // saying "fill in your own path", not "nothing to name". Only a genuinely blank, non-placeholder
            // reference (malformed data — never something CockpitProjectResourceEntry.Create itself writes) is
            // left out here; SharedProjectBindingDialogViewModel is what turns a blank Reference into a question
            // row rather than a value to trust.
            Resources =
            [
                .. (definition.Resources ?? [])
                    .Where(resource => resource.Placeholder || !string.IsNullOrWhiteSpace(resource.Reference))
                    .Select(resource => new SharedProjectBindingResource(resource.Role, resource.Reference) { Label = resource.Label }),
            ],
            Checksum = checksum,
        };
    }
}
