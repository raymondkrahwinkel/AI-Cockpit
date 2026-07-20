using System.Runtime.CompilerServices;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Watches a session's tool calls (AC-116) and, when the agent creates or updates a YouTrack issue in a turn
/// whose message carried images, attaches those images to that issue — the whole feature, with no per-session
/// toggle or tracking. Gated by the global <see cref="YouTrackSettings.AutoAttachImages"/> setting. Reads the
/// turn's images off the read/observe surface (host-managed and turn-scoped), so it never attaches a stale
/// earlier turn's images; dedup is by the turn's image-set identity, so a create and an update to the same
/// issue in one turn attach once, while a later turn attaches its own images again.
/// </summary>
internal sealed class YouTrackAutoAttacher
{
    private readonly ICockpitHost _host;
    private readonly YouTrackSettings _settings;
    private readonly Func<YouTrackInstance, string, IReadOnlyList<SessionImageAttachment>, CancellationToken, Task<AttachOutcome>> _upload;

    // Per turn's image-set (weak key: the read/observe surface hands back the same list instance for the whole
    // turn, and drops it when the turn ends), the issue keys already attached to. So a second create/update to
    // the same issue in one turn is a no-op, and the entry is collected once the turn's images are gone — no
    // image bytes are held past the turn.
    private readonly ConditionalWeakTable<IReadOnlyList<SessionImageAttachment>, HashSet<string>> _attachedByTurn = new();

    /// <param name="upload">How images are uploaded to an issue — defaults to the real HTTP multipart upload; overridden in tests to observe the gate/dedup without a network call.</param>
    public YouTrackAutoAttacher(
        ICockpitHost host,
        YouTrackSettings settings,
        Func<YouTrackInstance, string, IReadOnlyList<SessionImageAttachment>, CancellationToken, Task<AttachOutcome>>? upload = null)
    {
        _host = host;
        _settings = settings;
        _upload = upload ?? ((instance, issueId, images, cancellationToken) =>
            YouTrackAttach.UploadAsync(new YouTrackClient(), instance, issueId, images, cancellationToken));
    }

    public async Task HandleAsync(SessionToolActivity activity)
    {
        if (!_settings.AutoAttachImages || activity.IsError || !YouTrackToolActivity.IsIssueCreateOrUpdate(activity.ToolName))
        {
            return;
        }

        // No image in this turn — nothing to attach, and nothing that needs explaining.
        var images = _host.Sessions.GetCurrentTurnImages(activity.PaneId);
        if (images.Count == 0)
        {
            return;
        }

        // From here on the turn had images and the agent touched a YouTrack issue, so a failure to attach is
        // worth a word: silently doing nothing would leave the operator wondering why the screenshot never landed.
        if (YouTrackToolResultParser.TryParse(activity.ResultContent) is not { } target)
        {
            _host.ShowToast("Could not read the issue from the YouTrack result, so the sent image(s) were not attached automatically.", PluginToastSeverity.Warning);
            return;
        }

        if (YouTrackInstanceResolver.Resolve(_settings.Instances, target.Host) is not { } instance)
        {
            _host.ShowToast($"Could not match a configured YouTrack instance for {target.IssueId}, so the sent image(s) were not attached.", PluginToastSeverity.Warning);
            return;
        }

        // Dedup within the turn before the first await, so two create/update calls to the same issue in one turn
        // (both marshalled to this thread) never both upload. GetOrCreateValue keys on the list instance itself.
        var seen = _attachedByTurn.GetOrCreateValue(images);
        if (!seen.Add($"{instance.InstanceUrl}|{target.IssueId}"))
        {
            return;
        }

        var outcome = await _upload(instance, target.IssueId, images, CancellationToken.None);
        foreach (var error in outcome.Errors)
        {
            _host.ShowToast($"{target.IssueId}: could not attach an image — {error}", PluginToastSeverity.Error);
        }

        if (outcome.Attached > 0)
        {
            _host.ShowToast($"Attached {outcome.Attached} image{(outcome.Attached == 1 ? "" : "s")} to {target.IssueId}.", PluginToastSeverity.Success);
        }
    }
}
