using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// What a session started straight from a project opens with (AC-162/AC-164) — the New-session dialog's
// answers, reached without showing it. Both the launcher's Start button and the sidebar's ▶ come through
// here so they can't drift apart. Composes a `NewSessionResult` only; starting it stays the single launch path.
public sealed class ProjectQuickStart(ISessionProfileStore profiles, IProjectStartComposer composer) : ISingletonService
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

    // AC-1439: what the project decides is the launcher's compose (`SessionStartComposer`); this puts it in the
    // dialog's terms, with the app defaults for the typed Claude vocabulary, as pressing Start would have.
    public async Task<NewSessionResult> ComposeAsync(Project project, SessionProfile profile, CancellationToken cancellationToken = default)
    {
        var start = await composer.ComposeAsync(project, profile, cancellationToken).ConfigureAwait(true);
        var isSdk = start.Kind == PaneSessionKind.Sdk;
        return new NewSessionResult(
            isSdk ? SessionKind.Sdk : SessionKind.Tty,
            profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            project.Name,
            start.EnabledMcpServerNames,
            start.WorkingDirectory,
            PluginTtyOptions: isSdk ? null : profile.Defaults?.OptionDefaults,
            SdkLaunchOptions: isSdk ? profile.Defaults?.OptionDefaults : null,
            IsolateInWorktree: start.IsolateInWorktree,
            ReadingLevel: isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId: project.Id,
            SystemPrompt: start.SystemPrompt)
        {
            // The project's name, taken not typed (#AC-324).
            NameIsComposed = true,
        };
    }
}
