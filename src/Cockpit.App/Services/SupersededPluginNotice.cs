using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

/// <summary>
/// Tells the operator when a plugin they have has been replaced by others in this build, and offers to remove
/// it — asked, never done for them (Raymond, 2026-07-15, choosing this over cleaning up silently: nothing
/// disappears from their plugins folder behind their back).
/// <para>
/// It has to be said rather than left alone: the successors keep the widget type ids their predecessor
/// registered, so a saved dashboard survives the split — and so the old plugin and the new one claim the same
/// types. The registry refuses the second claim, which keeps the gallery honest, but it also means one of the
/// two plugins is doing nothing while looking installed. That is worth one sentence.
/// </para>
/// </summary>
internal sealed class SupersededPluginNotice(
    PluginManager plugins,
    IPluginRegistrationStore registrations,
    IPluginInstaller installer,
    IToastService toasts,
    ILogger<SupersededPluginNotice> logger) : ISingletonService
{
    /// <summary>
    /// Says something if there is something to say. Safe to call on every start: it goes quiet the moment the
    /// operator acts, because the condition it asks about is the old plugin still being loaded.
    /// </summary>
    /// <remarks>
    /// It asks the plugin manager what actually loaded, which is the only thing that makes the sentence true. It
    /// used to ask the registration store for its keys — a different question with a different answer: a
    /// registration means the plugin has been consented to, and one that is switched off, or whose pinned hash no
    /// longer matches its assembly, is never loaded (<see cref="PluginLoadPolicy"/> returns <c>Disabled</c> or
    /// <c>NeedsConsent</c>). None of those claim a widget type, so none of them are competing for one, and there
    /// is nothing to say — while offering to remove one talks over a decision the operator made on purpose.
    /// </remarks>
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

                toasts.Show(
                    $"'{superseded.DisplayName}' has been split up and its replacements are installed. It no longer does anything — remove it?",
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
