using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

// AC-1392: `ICockpitActions` for a backend. Delegation and starting a session need no window; there is no selected
// session, clipboard or operator to confirm with (D6), so those answer "nothing" — and a confirmation is refused,
// never assumed. The desktop's PluginActions has them, and hands delegation back to this.
public sealed class PluginBackendActions(
    ISessionProfileStore profileStore,
    IDelegationService delegation,
    ISessionLauncher? launcher = null) : ICockpitActions
{
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(500);

    public bool HasActiveSession => false;

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(false);

    // #67, #69: hands work to another profile as a background task via the cockpit's own delegation service,
    // so it is refused by the same rules and shows up in the delegated-tasks view like any agent's delegation.
    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null) =>
        DelegateAsync(profileLabel, prompt, workingDirectory, timeout, permission: null);

    // AC-971: `permission` left null runs the task read-only, whatever the target profile would allow — a plugin
    // that wants a task to change files says so, the same as an agent does on delegate_task.
    public async Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory, TimeSpan? timeout, string? permission)
    {
        var task = await delegation
            .DelegateAsync(new DelegationRequest(profileLabel, prompt, WorkingDirectory: workingDirectory, RequestedPermission: permission))
            .ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultPatience);

        // Polled rather than awaited on an event: the service's TasksChanged says *something* changed, and turning
        // that into "my task finished" is a subscription this call would have to unwind on every exit path. Half a
        // second of latency on a task that takes minutes is not worth that.
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (delegation.GetTask(task.TaskId) is not { } current)
            {
                throw new InvalidOperationException($"The task handed to '{profileLabel}' disappeared before it answered.");
            }

            switch (current.Status)
            {
                case DelegatedTaskStatus.Completed:
                    return current.Result ?? string.Empty;

                case DelegatedTaskStatus.Failed:
                    throw new InvalidOperationException($"'{profileLabel}' failed: {current.Error ?? "no reason given"}");

                case DelegatedTaskStatus.Stopped:
                    throw new InvalidOperationException($"The task handed to '{profileLabel}' was stopped.");
            }

            await Task.Delay(Beat).ConfigureAwait(false);
        }

        // The task is left running: it is real work, it is visible in the tasks view, and killing it because the
        // caller grew impatient would throw away whatever it had done.
        throw new TimeoutException($"'{profileLabel}' had not answered after {(timeout ?? DefaultPatience).TotalMinutes:0} minutes. The task is still running — it is in the delegated tasks view.");
    }

    // Both overloads are implemented, and the unnamed one delegates to the named one — never the other way around.
    // The interface's defaults run in the opposite direction, so an implementation that delegated the same way they
    // do would call itself until the stack ran out (#AC-312).
    public Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        StartSessionAsync(profileLabel, prompt, workingDirectory, null);

    // #69 without a window: the first Sessions desk, decided under the launcher's exclusion; the start checks again
    // that the desk still holds sessions and answers null when it no longer does.
    public async Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName)
    {
        if (launcher is null)
        {
            throw new NotSupportedException("This host cannot start sessions.");
        }

        var profiles = await profileStore.LoadAsync().ConfigureAwait(false);
        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                profiles.Count == 0
                    ? "No session profiles are configured."
                    : $"No profile is called '{profileLabel}'. There is: {string.Join(", ", profiles.Select(candidate => candidate.Label))}.");

        var workspaceId = await launcher.RunExclusiveAsync(() =>
                launcher.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no Sessions desk to start a session on.");

        var started = await launcher.StartSessionAsync(new SessionLaunchRequest(
                workspaceId, profile, prompt, workingDirectory, sessionName,
                Kind: null, LaunchOptions: null, IsolateInWorktree: null, ProjectId: null, StartedByTheAssistant: false))
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The session on '{profileLabel}' could not start.");

        return started.Name;
    }
}
