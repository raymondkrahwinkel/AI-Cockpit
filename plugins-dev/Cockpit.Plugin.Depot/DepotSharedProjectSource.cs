using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// One Depot connection's own `ISharedProjectSource` (AC-245): lists projects via `list_projects`, then reads
// each one's `.cockpit/project.json` (AC-244) for name/description — one without it isn't offered here.
// ponytail: one MCP round trip per project every workspace load, no caching; fine for a handful of projects.
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

            // Until Depot lets a Viewer get MCP read rights (DEP-side fix, not shipped), Depot's access guard
            // makes every Viewer's read of .cockpit/project.json fail regardless of whether it exists — report
            // that as visible-but-unreadable rather than silently dropped; Editor/Owner failure still means "not shared this way".
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

    // Reads `.cockpit/project.json` a second time (AC-246): `ListAsync`'s read only kept Name/Description,
    // binding needs the rest (GitUrl, BehaviorPrompt, worktree switch, MCP overlay, resource rows). `id`'s
    // `"{scheme}:{slug}"` is parsed with a plain prefix check since this plugin can't reference `Cockpit.Core`.
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
            else
            {
                // A failed download costs the picture, not the bind (SharedProjectBinding.LogoBytes' own remarks) —
                // but AC-1054: it must not do so silently, or "download failed" is indistinguishable from "no logo set".
                host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot")
                    .LogWarning("Downloading {Slug}'s logo failed: {Reason}", slug, download.Error);
            }
        }

        return SharedProjectBindingResult.Success(binding);
    }

    // AC-247. Re-reads id's current definition first — not for its checksum (the caller's baseChecksum is what
    // WriteAsync defends), but so GitUrl/Resources/Logo carry through byte-for-byte rather than being
    // reconstructed from SharedProjectBinding's lossy read shape (a Placeholder row would otherwise be dropped).
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
            // Used to fall back to `current.McpOverlay` on null, misreading "no opinion, every server ticked"
            // as "operator didn't touch this" — clearing a restriction by re-ticking every server sends null
            // on purpose. Always reflect what edit actually says, never fall back to what was already there.
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

        // AC-620 decision 4: only a project the operator can already write to is offered — Depot has no
        // create_project fallback, so a Viewer's row would dead-end. AC-699: CanWrite answers that, not a
        // repeated role list — the earlier list missed "Admin" and emptied the dropdown.
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
            // AC-246 (Raymond, 2026-08-02): a Placeholder row's Reference is blank on purpose — "fill in your
            // own path", not "nothing to name". Only a genuinely blank, non-placeholder reference (malformed
            // data) is left out; SharedProjectBindingDialogViewModel turns a blank Reference into a question row.
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
