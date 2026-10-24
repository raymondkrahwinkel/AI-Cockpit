using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// One Depot connection's own shared projects. The shared project-list source supplies the REST definitions.
internal sealed class DepotSharedProjectSource(
    DepotConnectionRegistration connection, string scheme, ICockpitHost host, HttpClient? httpClient = null)
    : ISharedProjectSource
{
    private readonly DepotProjectAddressing _addressing = DepotProjectAddressing.For(host, connection, scheme, httpClient);
    private readonly DepotBranding _branding = DepotBranding.For(host);
    private readonly bool _loadBranding = httpClient is null;
    private DepotBrandingSnapshot _brand = DepotBrandingSnapshot.Default;
    private readonly DepotProjectListingSource _projects = httpClient is null
        ? DepotProjectListingSource.For(host, connection)
        : new DepotProjectListingSource(host, connection, httpClient);

    internal DepotSharedProjectSource(
        DepotConnectionRegistration connection,
        string scheme,
        ICockpitHost host,
        Events.DepotEvents events,
        HttpClient? httpClient = null)
        : this(connection, scheme, host, httpClient)
    {
        // Construction cannot await the subscription; the task logs startup failures itself.
        _ = _SubscribeAsync(events);
    }

    public event Action<string?>? Changed;

    public string Key => scheme;

    internal string McpServerName => connection.McpServerName;

    // Whoever signed in or out changes what this connection lists, so the catalog reads it again.
    internal void RaiseAccountChanged() => Changed?.Invoke(null);

    private async Task _SubscribeAsync(Events.DepotEvents events)
    {
        try
        {
            await events.SubscribeAsync(connection.Id, "projects", _OnEvent, () => Changed?.Invoke(null), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot")
                .LogWarning(exception, "Subscribing to project changes for {Connection} failed.", connection.Name);
        }
    }

    private void _OnEvent(Events.DepotEvent cockpitEvent)
    {
        if (string.Equals(cockpitEvent.Kind, "definitionChanged", StringComparison.Ordinal)
            && cockpitEvent.Address is { Length: > 0 } address)
        {
            Changed?.Invoke($"{scheme}:{address}");
        }
        else if (string.Equals(cockpitEvent.Kind, "accessChanged", StringComparison.Ordinal))
        {
            Changed?.Invoke(null);
        }
    }

    public string SourceName => $"{_brand.Name} — {connection.Name}";

    public byte[]? SourceLogo => _brand.Logo;

    public async Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken)
    {
        if (_loadBranding)
        {
            _brand = await _branding.GetAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        var listing = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listing.Outcome != DepotProjectListingOutcome.Success)
        {
            return listing.Outcome switch
            {
                DepotProjectListingOutcome.NotSignedIn => SharedProjectListResult.Failed("Sign in to this Depot connection to see its shared projects."),
                DepotProjectListingOutcome.Forbidden => SharedProjectListResult.Failed("You do not have access to this Depot connection's shared projects."),
                _ => SharedProjectListResult.Failed(listing.Error ?? "Depot did not return a list of projects."),
            };
        }

        await _addressing.ObserveAsync(listing.Projects, cancellationToken).ConfigureAwait(false);

        return listing.IncludesCockpit
            ? _FromEndpoint(listing.Projects, listing.Error)
            : await _ListFromMcpAsync(listing.Projects, cancellationToken).ConfigureAwait(false);
    }

    private static Task<SharedProjectListResult> _ListFromMcpAsync(
        IReadOnlyList<DepotProjectListing> listed, CancellationToken cancellationToken)
    {
        _ = listed;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SharedProjectListResult.Failed(
            "This Depot version does not expose project definitions through its REST API."));
    }

    private SharedProjectListResult _FromEndpoint(IReadOnlyList<DepotProjectListing> projects, string? error)
    {
        var shared = new List<SharedProject>();
        var unreadable = new List<UnreadableSharedProject>();
        var states = new List<SharedProjectReadStatus>();

        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.Slug) || string.Equals(project.Kind, "Brain", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = $"{scheme}:{project.Key}";
            var name = project.Name is { Length: > 0 } value ? value : project.Slug;
            var role = DepotProjectRoleParser.Parse(project.Role);
            var canWrite = project.Cockpit?.CanWrite ?? role.CanWrite();
            var organization = project.Organization is { Slug: { Length: > 0 } organizationSlug }
                ? new SharedProjectOrganization(organizationSlug, project.Organization.Name, project.Organization.Role)
                : null;
            var cockpit = project.Cockpit;
            var state = _StateFor(cockpit?.State);
            if (state != SharedProjectReadState.Ok || cockpit?.Definition is not { } definition)
            {
                states.Add(new SharedProjectReadStatus(id, name, role.ToDisplayString(), state == SharedProjectReadState.Ok ? SharedProjectReadState.Invalid : state));
                if (!canWrite)
                {
                    unreadable.Add(new UnreadableSharedProject(id, name, role.ToDisplayString()));
                }

                continue;
            }

            name = definition.Name is { Length: > 0 } definedName ? definedName : name;
            shared.Add(new SharedProject(id, name)
            {
                Description = definition.Description,
                Role = role.ToDisplayString(),
                CanWriteBack = canWrite,
                HasRepository = !string.IsNullOrWhiteSpace(definition.GitUrl),
                Organization = organization,
            });
            states.Add(new SharedProjectReadStatus(id, name, role.ToDisplayString(), SharedProjectReadState.Ok));
        }

        return new SharedProjectListResult(true, shared, error)
        {
            VisibleButUnreadable = unreadable,
            ProjectReadStates = states,
        };
    }

    private static SharedProjectReadState _StateFor(string? state) => state switch
    {
        "ok" => SharedProjectReadState.Ok,
        "absent" => SharedProjectReadState.Absent,
        "invalid" => SharedProjectReadState.Invalid,
        "tooLarge" => SharedProjectReadState.TooLarge,
        _ => SharedProjectReadState.Invalid,
    };

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

        var slug = await _addressing.ProjectArgumentAsync(id[prefix.Length..], cancellationToken).ConfigureAwait(false);
        var definitionResult = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection, slug, httpClient, cancellationToken).ConfigureAwait(false);

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
            var download = await CockpitProjectLogoBlob.DownloadAsync(host, connection, slug, httpClient, cancellationToken)
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

        var slug = await _addressing.ProjectArgumentAsync(id[prefix.Length..], cancellationToken).ConfigureAwait(false);
        var currentRead = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection, slug, httpClient, cancellationToken).ConfigureAwait(false);

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
                var upload = await CockpitProjectLogoBlob.UploadAsync(host, connection, slug, pngBytes, httpClient, cancellationToken)
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
                    var delete = await CockpitProjectLogoBlob.DeleteAsync(host, connection, slug, httpClient, cancellationToken).ConfigureAwait(false);
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
            Resources = _UpgradeReferences(current.Resources),
            Logo = logoPath,
        };

        var writeResult = await CockpitProjectDefinitionStore.WriteAsync(
            host, connection, slug, merged, baseChecksum, callerRole: null,
            httpClient: httpClient, cancellationToken: cancellationToken).ConfigureAwait(false);

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

    // AC-620: the same project list ListAsync uses, but kept unfiltered by "does it already carry a
    // definition" — a publish target is a container to write the *first* definition into, so a project that has
    // never opted into Cockpit sharing is exactly the common case here, not the one ListAsync leaves out.
    public async Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken)
    {
        var listing = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        switch (listing.Outcome)
        {
            case DepotProjectListingOutcome.NotSignedIn:
                return SharedProjectPublishTargetListResult.Failed("Sign in to this Depot connection to publish a project.");
            case DepotProjectListingOutcome.Success:
                break;
            default:
                return SharedProjectPublishTargetListResult.Failed(
                    listing.Error is { Length: > 0 } error ? error : "Depot did not return a list of projects.");
        }

        await _addressing.ObserveAsync(listing.Projects, cancellationToken).ConfigureAwait(false);

        // AC-620 decision 4: only a project the operator can already write to is offered — Depot has no
        // create_project fallback, so a Viewer's row would dead-end. AC-699: CanWrite answers that, not a
        // repeated role list — the earlier list missed "Admin" and emptied the dropdown.
        var targets = listing.Projects
            .Where(project => !string.Equals(project.Kind, "Brain", StringComparison.OrdinalIgnoreCase))
            .Select(project => (Key: project.Key, project.Name, Role: DepotProjectRoleParser.Parse(project.Role)))
            .Where(entry => entry.Role.CanWrite())
            .Select(entry => new SharedProjectPublishTarget(
                $"{scheme}:{entry.Key}",
                entry.Name is { Length: > 0 } ? entry.Name : entry.Key,
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

        var slug = await _addressing.ProjectArgumentAsync(targetId[prefix.Length..], cancellationToken).ConfigureAwait(false);
        // This read gives the operator a useful "already published" answer. The write still carries
        // If-None-Match: *, so a concurrent first publish loses with a conflict instead of overwriting.
        var existing = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection, slug, httpClient, cancellationToken).ConfigureAwait(false);

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
            var upload = await CockpitProjectLogoBlob.UploadAsync(host, connection, slug, logoBytes, httpClient, cancellationToken)
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
            Resources = filtered.Portable.Count == 0 ? null : _UpgradeReferences([.. filtered.Portable]),
            Logo = logoPath,
        };

        var writeResult = await CockpitProjectDefinitionStore.WriteAsync(
            host, connection, slug, toWrite, baseChecksum: null, callerRole: null,
            httpClient: httpClient, cancellationToken: cancellationToken).ConfigureAwait(false);

        return writeResult.Outcome switch
        {
            PluginMcpToolCallOutcome.Success => SharedProjectPublishResult.Success(targetId),
            _ when writeResult.FailureKind == CockpitProjectDefinitionWriteFailureKind.PermissionDenied =>
                SharedProjectPublishResult.PermissionDenied(
                    writeResult.Error is { Length: > 0 } error ? error : "You do not have permission to publish here."),
            _ => SharedProjectPublishResult.Failed(
                writeResult.Error is { Length: > 0 } error ? error : "Depot did not confirm the write."),
        };
    }

    // Shared by PrepareBindingAsync and WriteBackAsync's own conflict snapshot — both ever have to turn a
    // CockpitProjectDefinition into the plugin-shape-agnostic SharedProjectBinding the same way.
    private List<CockpitProjectResourceEntry>? _UpgradeReferences(List<CockpitProjectResourceEntry>? resources)
    {
        foreach (var resource in resources ?? [])
        {
            resource.Reference = _addressing.UpgradeReference(resource.Reference);
        }

        return resources;
    }

    private SharedProjectBinding _ToBinding(string slug, CockpitProjectDefinition definition, string? checksum)
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
                    .Select(resource => new SharedProjectBindingResource(resource.Role, _addressing.UpgradeReference(resource.Reference)) { Label = resource.Label }),
            ],
            Checksum = checksum,
        };
    }
}
