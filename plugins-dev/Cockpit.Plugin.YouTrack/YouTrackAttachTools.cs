using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The explicit fallback for attaching a message's images to an issue (AC-116), exposed to the agent as
/// <c>mcp__cockpit-youtrack__attach_message_images_to_issue</c>. The automatic path (a create/update tool
/// completing in an image-bearing turn) covers the normal case; this tool is the safety net for a turn that has
/// an image but no create/update. Always available (independent of the global auto-attach switch, since calling
/// it is a deliberate act), and registered only while an instance is configured. The agent passes its own
/// session id (COCKPIT_PANE_ID) so the images attached are the ones its message carried, not another session's.
/// </summary>
internal sealed class YouTrackAttachTools
{
    private readonly ICockpitHost _host;
    private readonly YouTrackSettings _settings;
    private readonly YouTrackClient _client = new();

    public YouTrackAttachTools(ICockpitHost host, YouTrackSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    [McpServerTool(Name = "attach_message_images_to_issue")]
    [Description("Attaches image(s) to a YouTrack issue: either the image(s) the operator sent with the current message, or one file named by `path` (AC-170) — pass `path` when the image is not a message attachment, e.g. a screenshot the operator just pasted into the terminal (written under an exclr8-terminal-paste temp folder rather than sent as a message) or an image file the agent itself produced in this session's working directory. `path` must resolve inside one of those two folders — anything else is refused — and must actually be an image (checked by content, not by its extension). Use this tool when you are not creating or updating the issue this turn; when you do create or update one, the cockpit attaches the message's images automatically and you need not call this for those. Pass the value of the COCKPIT_PANE_ID environment variable in this session as `session`, so the images attached are the ones your message carried and not another session's.")]
    public async Task<string> AttachMessageImagesToIssue(
        [Description("The YouTrack issue id to attach the current message's images to, e.g. \"AC-116\".")] string issueId,
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("Optional: an absolute path to a single image file to attach instead of (or in addition to) the message's images — e.g. a terminal-pasted screenshot under an exclr8-terminal-paste temp folder, or a file this session wrote in its own working directory. Must resolve inside one of those two folders and be a genuine image file; anything else is refused. Omit to attach only the message's images, unchanged from before this argument existed.")] string? path = null)
    {
        var id = issueId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return "No issue id was given.";
        }

        // AC-128: act on the transport-verified caller pane, not the agent-declared `session` — otherwise an agent
        // could read another session's current-turn images by naming its id (confused deputy) and upload them to an
        // issue. The `session` argument is a fallback only off the verified path (in-process/tests), where there is
        // no MCP caller context to trust.
        var caller = _host.CurrentMcpCallerPaneId ?? session?.Trim();
        if (string.IsNullOrWhiteSpace(caller))
        {
            return "No session id was given — pass the COCKPIT_PANE_ID from this session's own environment as `session`.";
        }

        var images = new List<SessionImageAttachment>(_host.Sessions.GetCurrentTurnImages(caller));

        if (!string.IsNullOrWhiteSpace(path))
        {
            // AC-170: `path` is a genuine outbound channel (an agent names a file and its bytes leave the
            // machine), so it gets the same allow-list + canonical-containment + content-sniff scrutiny any other
            // filesystem-facing tool input would — see AttachImagePathGuard / ImageContentSniffer for the "why".
            if (!AttachImagePathGuard.TryResolve(path, _host.Sessions.ActiveSessionWorkingDirectory, out var resolvedPath, out var pathError))
            {
                return pathError;
            }

            if (!ImageContentSniffer.TryDetectMediaType(resolvedPath, out var mediaType))
            {
                return $"\"{path}\" is not a recognized image file — its content, not its extension, was checked.";
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(resolvedPath, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"Could not read \"{path}\": {exception.Message}";
            }

            images.Add(new SessionImageAttachment(mediaType, Convert.ToBase64String(bytes), Path.GetFileName(resolvedPath)));
        }

        if (images.Count == 0)
        {
            // AC-170 #1: reachable only when `path` was not given (a given `path` either errors out above or adds
            // an image) — so this stays byte-for-byte the original message and a caller that never passes `path`
            // cannot tell this tool changed at all.
            return "The current message carried no images to attach.";
        }

        if (YouTrackInstanceResolver.Resolve(_settings.Instances, host: null) is not { } instance)
        {
            return "Could not tell which YouTrack instance to attach to. Configure a YouTrack instance in the plugin settings; with several configured, this fallback cannot choose between them.";
        }

        var outcome = await YouTrackAttach.UploadAsync(_client, instance, id, images, CancellationToken.None);
        if (outcome.Attached == 0)
        {
            // Keep the raw YouTrack reason on a toast for the operator; hand the agent a concise result, not the server's body.
            foreach (var error in outcome.Errors)
            {
                _host.ShowToast($"{id}: could not attach an image — {error}", PluginToastSeverity.Error);
            }

            return $"Could not attach the image(s) to {id} — the reason was shown to the operator.";
        }

        var failed = outcome.Errors.Count > 0 ? $" ({outcome.Errors.Count} could not be attached — reason shown to the operator)" : string.Empty;
        foreach (var error in outcome.Errors)
        {
            _host.ShowToast($"{id}: could not attach an image — {error}", PluginToastSeverity.Error);
        }

        return $"Attached {outcome.Attached} image{(outcome.Attached == 1 ? "" : "s")} to {id}.{failed}";
    }
}
