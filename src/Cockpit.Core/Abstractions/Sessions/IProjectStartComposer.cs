using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// What a project and a profile decide about a start, without the new-session dialog (AC-162, AC-1439).
/// </summary>
public interface IProjectStartComposer
{
    /// <summary>
    /// The start <paramref name="project"/> and <paramref name="profile"/> settle on: kind, folder, isolation, prompt and MCP servers.
    /// </summary>
    Task<ProjectStart> ComposeAsync(Project project, SessionProfile profile, CancellationToken cancellationToken = default);
}

// What a project and a profile decide about a start, reached without the dialog (AC-162/AC-164, AC-1439).
public sealed record ProjectStart(
    PaneSessionKind Kind,
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? SystemPrompt,
    IReadOnlySet<string> EnabledMcpServerNames);
