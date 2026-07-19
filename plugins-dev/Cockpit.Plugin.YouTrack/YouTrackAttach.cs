using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>How many of a message's images made it onto an issue, and the reasons any did not (AC-116).</summary>
internal sealed record AttachOutcome(int Attached, IReadOnlyList<string> Errors);

/// <summary>
/// Uploads a message's images to an issue (AC-116), shared by the automatic path (a create/update tool
/// completing) and the explicit fallback tool. Each image is uploaded independently, so one failure does not
/// stop the rest; the caller decides how to report the outcome (a toast for the automatic path, a returned
/// string for the tool).
/// </summary>
internal static class YouTrackAttach
{
    public static async Task<AttachOutcome> UploadAsync(YouTrackClient client, YouTrackInstance instance, string issueId, IReadOnlyList<SessionImageAttachment> images, CancellationToken cancellationToken)
    {
        var attached = 0;
        var errors = new List<string>();
        foreach (var image in images)
        {
            try
            {
                var bytes = Convert.FromBase64String(image.Base64Data);
                await client.AttachFileAsync(instance.InstanceUrl, instance.Token, issueId, image.SuggestedFileName, bytes, image.MediaType, cancellationToken);
                attached++;
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
            }
        }

        return new AttachOutcome(attached, errors);
    }
}
