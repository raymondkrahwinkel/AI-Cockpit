using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Starts, stops and names sessions and keeps the desks they sit on, SDK and TTY alike (AC-1375).
/// Every decision over sessions and desks that reads more than one field goes through <see cref="RunExclusiveAsync{T}"/>.
/// </summary>
public interface ISessionLauncher
{
    /// <summary>
    /// Runs <paramref name="decision"/> under mutual exclusion with every other change to sessions and desks.
    /// Decide inside, act outside: each action below checks its own precondition again at the moment it runs.
    /// </summary>
    Task<T> RunExclusiveAsync<T>(Func<T> decision);

    /// <summary>
    /// The desks as they stand; read inside <see cref="RunExclusiveAsync{T}"/>.
    /// </summary>
    WorkspaceSettings Workspaces { get; }

    /// <summary>
    /// Whether the desk may be closed at all, the gate the tab's close button shows; read inside <see cref="RunExclusiveAsync{T}"/>.
    /// </summary>
    bool CanCloseWorkspace(string workspaceId);

    /// <summary>
    /// Whether <paramref name="profile"/> has a terminal route of its own, so it may start as a TTY session.
    /// </summary>
    bool ProfileHasTtyRoute(SessionProfile profile);

    /// <summary>
    /// The project with <paramref name="projectId"/>, or null when the cockpit has none by that id.
    /// </summary>
    Task<Project?> FindProjectByIdAsync(string projectId);

    /// <summary>
    /// Starts a session on the requested desk, or null when nothing could start — including when that desk no
    /// longer holds sessions by the time the pane would land on it.
    /// </summary>
    Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request);

    /// <summary>
    /// Closes the grid session with <paramref name="paneId"/>; a pane the grid does not hold is left alone.
    /// </summary>
    Task StopSessionAsync(string paneId);

    /// <summary>
    /// Renames the grid session with <paramref name="paneId"/>; false when the grid holds no such pane.
    /// </summary>
    Task<bool> SetSessionNameAsync(string paneId, string name);

    /// <summary>
    /// Creates a new Sessions desk named <paramref name="name"/>.
    /// </summary>
    Task<Workspace> CreateSessionsWorkspaceAsync(string name);

    /// <summary>
    /// Renames the desk with <paramref name="workspaceId"/>.
    /// </summary>
    Task RenameWorkspaceAsync(string workspaceId, string name);

    /// <summary>
    /// Counts the sessions on the desk and closes it only when that count is zero, in one step.
    /// </summary>
    /// <returns>
    /// The number of sessions found on it; zero means it was closed.
    /// </returns>
    Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId);

    /// <summary>
    /// Makes the assistant's own session, on no desk and outside <c>All</c>, as the registry's <c>Assistant</c> (AC-1379).
    /// Null when this cockpit cannot start sessions; the caller starts it and holds the only reference.
    /// </summary>
    IAssistantSession? CreateAssistantSession();

    /// <summary>
    /// Lets go of an assistant session its host stood down; the registry forgets it only while it is still the one held.
    /// </summary>
    void ReleaseAssistantSession(IAssistantSession session);
}

// AC-1375: one start as AssistantAgentGateway composes it. Kind null means the profile's own route; Kind Tty is only
// asked for once ProfileHasTtyRoute said yes, so both routes start through this one request.
public sealed record SessionLaunchRequest(
    string WorkspaceId,
    SessionProfile Profile,
    string? Prompt,
    string? WorkingDirectory,
    string? SessionName,
    PaneSessionKind? Kind,
    IReadOnlyDictionary<string, string>? LaunchOptions,
    bool? IsolateInWorktree,
    string? ProjectId,
    bool StartedByTheAssistant);

// PromptDelivered: null when no prompt was given, false when it is held until the session is ready.
public sealed record LaunchedSession(string PaneId, string Name, bool? PromptDelivered);
