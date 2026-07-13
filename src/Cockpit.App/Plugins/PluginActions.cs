using Avalonia.Input.Platform;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// <see cref="ICockpitActions"/> a plugin uses to act on the cockpit: inject text into the selected
/// session (reusing the session's own per-kind input seam), put text on the clipboard, and ask the operator
/// to confirm a destructive action. The clipboard is resolved lazily via a factory so this has no hard
/// dependency on a window being up.
/// </summary>
public sealed class PluginActions(CockpitViewModel cockpit, Func<IClipboard?> clipboardFactory, ISessionDialogService dialogService, ISessionProfileStore profileStore) : ICockpitActions
{
    public bool HasActiveSession => cockpit.SelectedSession is not null;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
        dialogService.ShowConfirmationDialogAsync(title, message, confirmLabel);

    public Task InjectIntoActiveSessionAsync(string text)
    {
        cockpit.SelectedSession?.InjectText(text);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a session on a named profile and hands it a prompt (#69) — the same act as the New-session dialog, minus
    /// the dialog. The profile's own defaults are used for model, permissions and effort, because a caller who names
    /// a profile means "the way I set that one up".
    /// </summary>
    public async Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null)
    {
        var profiles = await profileStore.LoadAsync().ConfigureAwait(false);

        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                profiles.Count == 0
                    ? "No session profiles are configured."
                    : $"No profile is called '{profileLabel}'. There is: {string.Join(", ", profiles.Select(candidate => candidate.Label))}.");

        var name = await cockpit.StartSessionForPluginAsync(profile, prompt, workingDirectory).ConfigureAwait(false);

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
