using Avalonia.Input.Platform;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Delegation;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// <see cref="ICockpitActions"/> a plugin uses to act on the cockpit: inject text into the selected
/// session (reusing the session's own per-kind input seam), put text on the clipboard, and ask the operator
/// to confirm a destructive action. The clipboard is resolved lazily via a factory so this has no hard
/// dependency on a window being up.
/// </summary>
public sealed class PluginActions(
    CockpitViewModel cockpit,
    Func<IClipboard?> clipboardFactory,
    ISessionDialogService dialogService,
    ISessionProfileStore profileStore,
    IDelegationService delegation) : ICockpitActions
{
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(500);

    public bool HasActiveSession => cockpit.SelectedSession is not null;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
        dialogService.ShowConfirmationDialogAsync(title, message, confirmLabel);

    public Task InjectIntoActiveSessionAsync(string text)
    {
        cockpit.SelectedSession?.InjectText(text);
        return Task.CompletedTask;
    }

    // AC-577, no fast path — deliberately. This writes to the on-screen session (statusline, name), so the
    // marshalling is the whole job and an inline branch would let a caller off the UI thread mutate a bound
    // property directly. The consequence is written down rather than discovered: PluginActions must never be
    // constructed in a process without a dispatcher loop, and a test that claims to cover this line belongs in
    // Cockpit.App.ViewTests — Cockpit.Core.Tests can no longer name Avalonia.Threading at all.
    public Task SetActiveSessionStatusAsync(string? statusline = null, string? name = null) =>
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cockpit.SelectedSession is { } session)
            {
                if (statusline is not null)
                {
                    session.Statusline = statusline;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    // A flow naming the session it started is a name somebody chose, same as a rename — so a ticket
                    // linked to that session later offers its name rather than taking it (#AC-310).
                    session.SetNameDirectly(name);
                }
            }
        }).GetTask();

    /// <summary>
    /// Hands work to another profile as a background task and waits for the answer (#67, #69). The task goes through
    /// the cockpit's own delegation service, so it is refused by the same rules an agent's delegation is refused by,
    /// and it shows up in the delegated-tasks view — a plugin does not get a quieter way to run an agent than an agent
    /// has.
    /// </summary>
    public async Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var task = await delegation
            .DelegateAsync(new DelegationRequest(profileLabel, prompt, WorkingDirectory: workingDirectory))
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

    /// <summary>
    /// Opens a session on a named profile and hands it a prompt (#69) — the same act as the New-session dialog, minus
    /// the dialog. The profile's own defaults are used for model, permissions and effort, because a caller who names
    /// a profile means "the way I set that one up". <paramref name="sessionName"/> names it as it opens (#AC-312);
    /// blank leaves the naming to the profile and the clock.
    /// </summary>
    public async Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName)
    {
        var profiles = await profileStore.LoadAsync().ConfigureAwait(false);

        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                profiles.Count == 0
                    ? "No session profiles are configured."
                    : $"No profile is called '{profileLabel}'. There is: {string.Join(", ", profiles.Select(candidate => candidate.Label))}.");

        var name = await cockpit.StartSessionForPluginAsync(profile, prompt, workingDirectory, sessionName).ConfigureAwait(false);

        return name;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (clipboardFactory() is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
