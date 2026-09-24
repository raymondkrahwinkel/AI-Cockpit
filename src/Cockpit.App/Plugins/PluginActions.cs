using Avalonia.Input.Platform;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// `ICockpitActions` a plugin uses to act on the cockpit: inject text into the selected session, put text
// on the clipboard, and confirm a destructive action. Clipboard is resolved lazily so no window is required.
public sealed class PluginActions(
    CockpitViewModel cockpit,
    Func<IClipboard?> clipboardFactory,
    ISessionDialogService dialogService,
    ISessionProfileStore profileStore,
    PluginBackendActions backend) : ICockpitActions
{
    public bool HasActiveSession => cockpit.SelectedSession is not null;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
        dialogService.ShowConfirmationDialogAsync(title, message, confirmLabel);

    public Task InjectIntoActiveSessionAsync(string text)
    {
        cockpit.SelectedSession?.InjectText(text);
        return Task.CompletedTask;
    }

    // AC-577: always marshals to the UI thread (no fast path) since this mutates a bound property directly;
    // PluginActions must never be constructed in a process without a dispatcher loop.
    public Task SetActiveSessionStatusAsync(string? statusline = null, string? name = null) =>
        UiThreadCall.DispatchAsync(() =>
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
        });

    // #67, #69, AC-1392: delegation needs no window, so it is the backend's, whose rules and tasks view it shares.
    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null) =>
        backend.DelegateAsync(profileLabel, prompt, workingDirectory, timeout);

    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory, TimeSpan? timeout, string? permission) =>
        backend.DelegateAsync(profileLabel, prompt, workingDirectory, timeout, permission);

    // Both overloads are implemented, and the unnamed one delegates to the named one — never the other way around.
    // The interface's defaults run in the opposite direction, so an implementation that delegated the same way they
    // do would call itself until the stack ran out (#AC-312).
    public Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        StartSessionAsync(profileLabel, prompt, workingDirectory, null);

    // #69: opens a session on a named profile with a prompt — the New-session dialog's act, minus the dialog.
    // Uses the profile's own defaults for model/permissions/effort; `sessionName` blank leaves naming to it.
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
