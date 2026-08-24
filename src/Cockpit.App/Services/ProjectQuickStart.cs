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

// What a session started straight from a project opens with (AC-162/AC-164) — the New-session dialog's
// answers, reached without showing it. Both the launcher's Start button and the sidebar's ▶ come through
// here so they can't drift apart. Composes a `NewSessionResult` only; starting it stays the single launch path.
public sealed class ProjectQuickStart(
    ISessionProfileStore profiles,
    IMcpServerCatalog mcpServers,
    ITtySessionProviderResolver ttyProviders,
    IProjectMemorySourceRegistry memorySources) : ISingletonService
{
    // The session `project` starts, or `null` when it names no profile that still exists. Null is a fall-back
    // signal, not a failure: picking an arbitrary profile would silently start the wrong provider, so the
    // caller opens the dialog instead and lets the operator say which.
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

    // The same compose, for a caller that already knows its profile (AC-719: a spawn names it explicitly) rather
    // than asking the project's `DefaultProfileLabel` to pick one — this is the core `ComposeAsync(Project)`
    // resolves a profile for and then calls, so the two doors cannot drift apart.
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

        // Must be ResolveDefaultKind, not HasTtyRoute (AC-584): the latter is true for every Claude profile
        // regardless of saved kind, so using it would always start a TTY. The promise here is "the dialog,
        // skipped", so what starts must be what pressing Start would have started.
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

    // The servers this session opens with ticked, per the project's own choice, not the profile's (Raymond,
    // 2026-07-24). Always an explicit set, empty included, never `null` — downstream reads `null` as "no
    // selection" and falls back to the profile's saved one, which would put the profile back in charge.
    private async Task<IReadOnlySet<string>> _TickedServerNamesAsync(Project project, CancellationToken cancellationToken)
    {
        var catalog = await mcpServers.GetServersForProjectAsync(project.Id, cancellationToken).ConfigureAwait(true);

        return McpServerRegistryFilter.OfferedToOperator(catalog)
            .Where(server => project.McpOverlay.IsSelectedByDefault(server))
            .Select(server => server.Name)
            // The same comparer the rest of this feature matches names with: a casing difference between the
            // registry and a hand-written overlay would otherwise drop a server from the launch without a word.
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
