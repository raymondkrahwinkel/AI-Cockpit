using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// What a session started straight from a project opens with (AC-162/AC-164) — the answers the New-session dialog
/// would have arrived at, reached without showing it. The launcher's Start button and the sidebar's ▶ both come
/// through here, so the two cannot drift into starting subtly different sessions from the same project.
/// </summary>
/// <remarks>
/// Deliberately composes a <see cref="NewSessionResult"/> and nothing more: starting it stays the cockpit's single
/// launch path, which owns worktree isolation, the pane and the session's lifetime. This only answers "with what".
/// </remarks>
public sealed class ProjectQuickStart(
    ISessionProfileStore profiles,
    IMcpServerCatalog mcpServers,
    ITtySessionProviderResolver ttyProviders) : ISingletonService
{
    /// <summary>
    /// The session <paramref name="project"/> starts, or <see langword="null"/> when it names no profile that still
    /// exists. Null is not a failure to report but a fall-back signal: a session needs a profile to run at all, and
    /// picking an arbitrary one would silently start the wrong provider, so the caller opens the dialog instead and
    /// lets the operator say which.
    /// </summary>
    public async Task<NewSessionResult?> ComposeAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project.DefaultProfileLabel))
        {
            return null;
        }

        var configured = await profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        var profile = configured.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, project.DefaultProfileLabel, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return null;
        }

        var defaults = SessionStartDefaults.Resolve(project, profile);

        // The same rule the dialog opens on, from the same place: the promise here is "the dialog, skipped", so what
        // starts has to be what pressing Start would have started.
        var kind = SessionKindDefaults.HasTtyRoute(profile, ttyProviders) ? SessionKind.Tty : SessionKind.Sdk;
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
            await _TickedServerNamesAsync(project, defaults, cancellationToken).ConfigureAwait(true),
            defaults.WorkingDirectory,
            // A provider's own declared start defaults, saved on the profile — the same values the dialog's option
            // rows open on. Only ever for the kind actually starting: the two vocabularies never both apply.
            PluginTtyOptions: isSdk ? null : profile.Defaults?.OptionDefaults,
            SdkLaunchOptions: isSdk ? profile.Defaults?.OptionDefaults : null,
            IsolateInWorktree: defaults.IsolateInWorktree,
            ReadingLevel: isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId: project.Id,
            SystemPrompt: defaults.SystemPrompt);
    }

    /// <summary>
    /// The servers this session opens with ticked: the ones the project's catalog offers, narrowed to the profile's
    /// saved selection where it has one.
    /// </summary>
    /// <remarks>
    /// Always an explicit set, empty included, and never <see langword="null"/> — which downstream reads as "this
    /// launch made no selection" and answers by falling back to the profile's saved one over the <em>unscoped</em>
    /// registry. For a project that is the wrong answer: a project whose overlay leaves nothing to offer means this
    /// session gets nothing, and saying null there would hand it every server the project had just switched off.
    /// </remarks>
    private async Task<IReadOnlySet<string>> _TickedServerNamesAsync(
        Project project,
        SessionStartDefaults defaults,
        CancellationToken cancellationToken)
    {
        var catalog = await mcpServers.GetServersForProjectAsync(project.Id, cancellationToken).ConfigureAwait(true);
        var offered = McpServerRegistryFilter.OfferedToOperator(catalog).Select(server => server.Name);

        if (defaults.EnabledMcpServerNames is not { } restriction)
        {
            return offered.ToHashSet(StringComparer.Ordinal);
        }

        var selected = new HashSet<string>(restriction, StringComparer.OrdinalIgnoreCase);
        return offered.Where(selected.Contains).ToHashSet(StringComparer.Ordinal);
    }
}
