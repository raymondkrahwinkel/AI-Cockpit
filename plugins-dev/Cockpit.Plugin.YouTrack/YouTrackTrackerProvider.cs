using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The YouTrack half of the tracker-provider contract (AC-154): posts a comment, moves an issue's stage, and attaches
/// a file, resolving the instance (base URL + token) for an issue from the plugin's configured instances. Every action
/// returns whether it landed rather than throwing, so a failed post does not take a consumer's run down.
/// </summary>
internal sealed class YouTrackTrackerProvider(YouTrackSettings settings) : ITrackerProvider
{
    private readonly YouTrackClient _client = new();

    public string TrackerId => "youtrack";

    public async Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default)
    {
        if (_Instance() is not { } instance)
        {
            return false;
        }

        try
        {
            await _client.AddCommentAsync(instance.InstanceUrl, instance.Token, issueId, comment, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // A tracker action that fails degrades to "did not land" — the contract's whole point — never a crash of the run.
            return false;
        }
    }

    public async Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default)
    {
        if (_Instance() is not { } instance)
        {
            return false;
        }

        try
        {
            var issue = await _client.GetIssueAsync(instance.InstanceUrl, instance.Token, issueId, cancellationToken);
            var fields = await _client.GetIssueFieldsAsync(instance.InstanceUrl, instance.Token, issue, cancellationToken);
            if (fields.State is not { } field)
            {
                return false;
            }

            await _client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, field, stage, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default)
    {
        if (_Instance() is not { } instance)
        {
            return false;
        }

        try
        {
            await _client.AttachFileAsync(instance.InstanceUrl, instance.Token, issueId, fileName, content, mediaType, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default)
    {
        if (_Instance() is not { } instance)
        {
            return [];
        }

        try
        {
            return await _client.ReadCommentsAsync(instance.InstanceUrl, instance.Token, issueId, cancellationToken);
        }
        catch (Exception)
        {
            return [];
        }
    }

    // The sole configured instance (base URL + token both set), or null when none or more than one — Autopilot works
    // one instance in practice; a URL-matching resolver for multi-instance runs can layer on later.
    private YouTrackInstance? _Instance() =>
        settings.Instances.Where(instance => instance.InstanceUrl.Length > 0 && instance.Token.Length > 0).ToList() is [var only] ? only : null;
}
