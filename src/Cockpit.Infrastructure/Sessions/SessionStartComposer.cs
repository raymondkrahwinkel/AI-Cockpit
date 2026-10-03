using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// What a project and a profile decide about a start, reached without the dialog (AC-162/AC-164, AC-1439).
public sealed record ProjectStart(
    PaneSessionKind Kind,
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? SystemPrompt,
    IReadOnlySet<string> EnabledMcpServerNames);

// AC-1439: the half of a start that decides it, out of the desktop. The quick start and every spawn compose here;
// the dialog fills the same fields itself and hands them over composed.
public sealed class SessionStartComposer(
    IProjectStore projects,
    IMcpServerCatalog mcpServers,
    ITtySessionProviderResolver? ttyProviders = null,
    IProjectMemorySourceRegistry? memorySources = null,
    IWorktreeManager? worktrees = null) : ISingletonService
{
    // What starting on `project` under `profile` opens with: the kind pressing Start would have started (AC-584), the
    // project's folder, isolation and behaviour prompt, and its own ticked servers rather than the profile's.
    public async Task<ProjectStart> ComposeAsync(Project project, SessionProfile profile, CancellationToken cancellationToken = default)
    {
        var defaults = SessionStartDefaults.Resolve(
            project, profile,
            memorySources: memorySources?.Sources.ToMemorySources(),
            unresolvedReferences: ProjectResourceProbe.FindUnresolved(project.Resources),
            instructionContents: ProjectInstructionContentReader.Read(project.Resources));
        return new ProjectStart(
            TtyRoute.IsDefault(profile, ttyProviders) ? PaneSessionKind.Tty : PaneSessionKind.Sdk,
            defaults.WorkingDirectory,
            defaults.IsolateInWorktree,
            defaults.SystemPrompt,
            await _TickedServerNamesAsync(project, cancellationToken).ConfigureAwait(false));
    }

    // A spawn's request as the dialog would have filled it (AC-719, AC-773): the project named or found from the folder,
    // what it decides, and the profile's own options for the kind that starts. The caller's own words win.
    public async Task<SessionLaunchRequest> ComposeAsync(SessionLaunchRequest request, CancellationToken cancellationToken = default)
    {
        if (request.IsComposed)
        {
            return request;
        }

        var profile = request.Profile;
        var profileOnly = SessionStartDefaults.Resolve(project: null, profile);
        var lookupDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? profileOnly.WorkingDirectory : request.WorkingDirectory;
        var projectId = request.ProjectId is { Length: > 0 } given ? given : await ProjectIdForDirectoryAsync(lookupDirectory).ConfigureAwait(false);
        var project = projectId is null ? null : await FindProjectAsync(projectId).ConfigureAwait(false);
        var composed = project is null ? null : await ComposeAsync(project, profile, cancellationToken).ConfigureAwait(false);
        var kind = request.Kind ?? composed?.Kind ?? (TtyRoute.IsDefault(profile, ttyProviders) ? PaneSessionKind.Tty : PaneSessionKind.Sdk);
        var named = !string.IsNullOrWhiteSpace(request.SessionName);

        return request with
        {
            Kind = kind,
            SessionName = named ? request.SessionName : $"{profile.Label} — {DateTime.Now:HH:mm}",
            NameIsComposed = !named,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? composed?.WorkingDirectory ?? profileOnly.WorkingDirectory
                : request.WorkingDirectory,
            ProjectId = projectId,
            IsolateInWorktree = request.IsolateInWorktree ?? composed?.IsolateInWorktree ?? false,
            EnabledMcpServerNames = request.EnabledMcpServerNames ?? composed?.EnabledMcpServerNames,
            LaunchOptions = request.LaunchOptions ?? profile.Defaults?.OptionDefaults,
            SystemPrompt = request.SystemPrompt ?? composed?.SystemPrompt ?? profileOnly.SystemPrompt,
            IsComposed = true,
        };
    }

    // AC-544: the standing statusline instruction rides after the profile's own words, never instead of them; AC-490
    // adds the job report for a job's run.
    public static IReadOnlyDictionary<string, string> WithInstructions(
        IReadOnlyDictionary<string, string>? options, string? systemPrompt, bool isJobRun)
    {
        var merged = options is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(options, StringComparer.Ordinal);
        var standing = isJobRun
            ? AgentStatusSystemPrompt.Default + "\n\n" + AgentStatusSystemPrompt.JobRun
            : AgentStatusSystemPrompt.Default;
        merged[WellKnownPluginSessionOptions.AppendSystemPrompt] = string.IsNullOrWhiteSpace(systemPrompt)
            ? standing
            : systemPrompt.Trim() + "\n\n" + standing;
        return merged;
    }

    public async Task<Project?> FindProjectAsync(string projectId) =>
        (await projects.LoadAsync().ConfigureAwait(false)).Projects.FirstOrDefault(project => project.Id == projectId);

    // The folder as requested, never the worktree a start derives from it: a run's own worktree belongs to no project,
    // the repository it was cut from does (AC-320). An unreadable list or registry costs that answer, not the start.
    public async Task<string?> ProjectIdForDirectoryAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        IReadOnlyList<Project> known = [];
        IReadOnlyList<WorktreeRecord> registered = [];
        try
        {
            known = (await projects.LoadAsync().ConfigureAwait(false)).Projects;
            if (worktrees is not null)
            {
                registered = await worktrees.ListAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Neither read is worth a session: the answer is only "no project".
        }

        return EmbeddedSessionProject.Resolve(known, registered, directory)?.Id;
    }

    // Always an explicit set, empty included: null reads downstream as "no selection" and puts the profile back in
    // charge (Raymond, 2026-07-24). Matched with the comparer the rest of the feature uses.
    private async Task<IReadOnlySet<string>> _TickedServerNamesAsync(Project project, CancellationToken cancellationToken)
    {
        var catalog = await mcpServers.GetServersForProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
        return McpServerRegistryFilter.OfferedToOperator(catalog)
            .Where(server => project.McpOverlay.IsSelectedByDefault(server))
            .Select(server => server.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
