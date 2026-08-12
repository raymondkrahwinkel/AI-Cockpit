using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.App.Services;

// What a session started straight from a project opens with (AC-162/AC-164) — the answers the New-session dialog
// would have arrived at, reached without showing it. The launcher's Start button and the sidebar's ▶ both come
// through here, so the two cannot drift into starting subtly different sessions from the same project.
// Deliberately composes a `NewSessionResult` and nothing more: starting it stays the cockpit's single
// launch path, which owns worktree isolation, the pane and the session's lifetime. This only answers "with what".
public sealed class ProjectQuickStart(
    ISessionProfileStore profiles,
    IMcpServerCatalog mcpServers,
    ITtySessionProviderResolver ttyProviders,
    IProjectMemorySourceRegistry memorySources) : ISingletonService
{
    // The session `project` starts, or `null` when it names no profile that still
    // exists. Null is not a failure to report but a fall-back signal: a session needs a profile to run at all, and
    // picking an arbitrary one would silently start the wrong provider, so the caller opens the dialog instead and
    // lets the operator say which.
    public async Task<NewSessionResult?> ComposeAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project.DefaultProfileLabel))
        {
            return null;
        }

        var configured = await profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        var profile = configured.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, project.DefaultProfileLabel, StringComparison.OrdinalIgnoreCase));
        return profile is null ? null : await ComposeAsync(project, profile, cancellationToken).ConfigureAwait(true);
    }

    // The same compose, for a caller that already knows which profile it wants rather than asking the project's
    // own `DefaultProfileLabel` to name one (AC-719): a spawn started with `start_agent` names its profile
    // explicitly, so the project's saved default has nothing to add there and forcing one would refuse a project
    // that simply never set one. One seam, two doors — this is the core `ComposeAsync(Project)` resolves a profile
    // for and then calls; every other rule (isolation, the standing prompt, the project's own MCP selection) lives
    // here exactly once so the two doors cannot drift into starting subtly different sessions from one project.
    public async Task<NewSessionResult> ComposeAsync(Project project, SessionProfile profile, CancellationToken cancellationToken = default)
    {
        // The probe is I/O, which Resolve deliberately never does itself (see its own remarks on
        // unresolvedReferences) — a quick start is an actual launch, so it is worth the filesystem check a preview
        // field elsewhere in the dialog skips.
        var unresolvedReferences = ProjectResourceProbe.FindUnresolved(project.Resources);

        // Reading a ticked Instructions row's content (AC-486) is the same kind of I/O, and for the same reason
        // never done inside Resolve itself — this is one of the two launch call sites its own remarks name.
        var instructionContents = ProjectInstructionContentReader.Read(project.Resources);
        var defaults = SessionStartDefaults.Resolve(
            project, profile,
            memorySources: memorySources.Sources.ToMemorySources(),
            unresolvedReferences: unresolvedReferences,
            instructionContents: instructionContents);

        // The same rule the dialog opens on, from the same place: the promise here is "the dialog, skipped", so what
        // starts has to be what pressing Start would have started. That has to be ResolveDefaultKind and not
        // HasTtyRoute (AC-584): the latter only answers whether a TUI exists at all, which is true for every Claude
        // profile, so asking it started a TTY however the profile had saved its kind — the one setting this line is
        // supposed to be reading.
        var kind = SessionKindDefaults.ResolveDefaultKind(profile, ttyProviders);
        var isSdk = kind == SessionKind.Sdk;

        return new NewSessionResult(
            kind,
            profile,
            // The typed Claude vocabulary is migration-only and the dialog seeds it with app defaults whatever the
            // profile says; a quick start has no operator to override them either, so it does the same.
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            project.Name,
            await _TickedServerNamesAsync(project, cancellationToken).ConfigureAwait(true),
            defaults.WorkingDirectory,
            // A provider's own declared start defaults, saved on the profile — the same values the dialog's option
            // rows open on. Only ever for the kind actually starting: the two vocabularies never both apply.
            PluginTtyOptions: isSdk ? null : profile.Defaults?.OptionDefaults,
            SdkLaunchOptions: isSdk ? profile.Defaults?.OptionDefaults : null,
            IsolateInWorktree: defaults.IsolateInWorktree,
            ReadingLevel: isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId: project.Id,
            SystemPrompt: defaults.SystemPrompt)
        {
            // The project's name, taken not typed. Said here rather than left to whoever launches this, so the
            // result is right on its own and a second caller cannot inherit the old bug (#AC-324).
            NameIsComposed = true,
        };
    }

    // The servers this session opens with ticked: everything the checklist would have offered, minus the ones the
    // project switched off. The project's choice, not the profile's — a project says which servers it works with,
    // and that is the answer wherever it has one (Raymond, 2026-07-24).
    // Always an explicit set, empty included, and never `null` — which downstream reads as "this
    // launch made no selection" and answers by falling back to the profile's saved one. That would quietly put the
    // profile back in charge of a session started from a project.
    private async Task<IReadOnlySet<string>> _TickedServerNamesAsync(Project project, CancellationToken cancellationToken)
    {
        var catalog = await mcpServers.GetServersForProjectAsync(project.Id, cancellationToken).ConfigureAwait(true);

        return McpServerRegistryFilter.OfferedToOperator(catalog)
            .Where(server => project.McpOverlay.IsSelectedByDefault(server.Name))
            .Select(server => server.Name)
            // The same comparer the rest of this feature matches names with: a casing difference between the
            // registry and a hand-written overlay would otherwise drop a server from the launch without a word.
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
