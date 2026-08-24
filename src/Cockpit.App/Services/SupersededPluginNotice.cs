using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// Tells the operator when a plugin has been replaced by others in this build, and offers to remove it — asked,
// never done for them, so nothing disappears from their plugins folder behind their back. Needed because successors
// keep the predecessor's widget type ids, so the registry refuses its claim and it keeps looking installed while doing nothing.
internal sealed class SupersededPluginNotice(
    PluginManager plugins,
    IPluginRegistrationStore registrations,
    IPluginInstaller installer,
    IToastService toasts,
    ILogger<SupersededPluginNotice> logger) : ISingletonService
{
    // Says something if there is something to say. Safe to call on every start: it goes quiet once the operator
    // acts, since it asks the plugin manager what actually loaded rather than the registration store — a
    // registered-but-not-loaded plugin (disabled, or a hash mismatch) claims no widget type and needs no notice.
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = plugins.Loaded.Select(plugin => plugin.FolderId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var superseded in SupersededPlugin.Known.Where(plugin => plugin.ShouldOffer(loaded)))
            {
                logger.LogInformation(
                    "Plugin '{Plugin}' has been superseded by {Successors}; offering to remove it",
                    superseded.Id, string.Join(", ", superseded.SuccessorIds));

                // Split or merged — AC-836 made the second kind real, and the operator only needs to know the
                // work moved to a plugin they already have.
                toasts.Show(
                    $"'{superseded.DisplayName}' has been replaced by a plugin you already have installed. It no longer does anything — remove it?",
                    ToastSeverity.Information,
                    "Remove",
                    () => _ = _RemoveAsync(superseded, cancellationToken));
            }
        }
        catch (Exception exception)
        {
            // A notice is a courtesy. Failing to work out whether to show one is not a reason to hold up a
            // cockpit that is otherwise fine.
            logger.LogWarning(exception, "Could not check for superseded plugins");
        }
    }

    private async Task _RemoveAsync(SupersededPlugin superseded, CancellationToken cancellationToken)
    {
        try
        {
            // The same two steps the plugin manager's own Remove takes: staged now, gone on the next start,
            // because a loaded assembly cannot be deleted underneath itself on Windows.
            await installer.MarkForRemovalAsync(superseded.Id, cancellationToken).ConfigureAwait(false);
            await registrations.RemoveAsync(superseded.Id, cancellationToken).ConfigureAwait(false);

            toasts.Show($"'{superseded.DisplayName}' will be gone after the next restart.", ToastSeverity.Information);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove the superseded plugin '{Plugin}'", superseded.Id);
            toasts.Show($"Could not remove '{superseded.DisplayName}'. It can be removed from Options → Plugins.", ToastSeverity.Error);
        }
    }
}
