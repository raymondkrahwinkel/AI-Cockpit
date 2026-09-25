using System.Text.Json;
using Cockpit.Plugin.YouTrack.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.YouTrack.UI;

// AC-1397: the UI part's one door to the backend part. Each call puts its request on the plugin's channel and reads
// the answer back, so no control holds a YouTrackClient of its own; a session-bound call names its pane.
internal sealed class YouTrackBackend(IPluginUiChannel channel)
{
    public async Task<IReadOnlyList<YouTrackProject>> ProjectsAsync(YouTrackInstance instance) =>
        await _AskAsync<List<YouTrackProject>>(YouTrackChannel.Projects, new InstanceRequest(instance)) ?? [];

    public async Task<IReadOnlyList<string>> PreferredTagsAsync(string? paneId, string? defaultProjectTag) =>
        await _AskAsync<List<string>>(YouTrackChannel.PreferredTags, new PreferredTagsRequest(paneId, defaultProjectTag)) ?? [];

    public async Task<IReadOnlyList<YouTrackIssue>> IssuesAsync(YouTrackInstance instance, IReadOnlyList<string>? projectTags, string? filter, bool assignedToMe, int top) =>
        await _AskAsync<List<YouTrackIssue>>(YouTrackChannel.Issues, new IssuesRequest(instance, projectTags, filter, assignedToMe, top)) ?? [];

    public async Task<ProjectStateFieldAnswer> ProjectStateFieldAsync(YouTrackInstance instance, string projectShortName) =>
        await _AskAsync<ProjectStateFieldAnswer>(YouTrackChannel.ProjectStateField, new ProjectStateFieldRequest(instance, projectShortName))
            ?? new ProjectStateFieldAnswer(null, []);

    public async Task<YouTrackIssueFields> IssueFieldsAsync(YouTrackInstance instance, YouTrackIssue issue) =>
        await _AskAsync<YouTrackIssueFields>(YouTrackChannel.IssueFields, new IssueRequest(instance, issue))
            ?? new YouTrackIssueFields(null, null);

    public Task SetStateAsync(YouTrackInstance instance, YouTrackIssue issue, YouTrackStateField state, string target, string? paneId) =>
        _AskAsync<bool>(YouTrackChannel.SetState, new SetStateRequest(instance, issue, state, target, paneId));

    public async Task<string> StartAsync(YouTrackInstance instance, YouTrackIssue issue, YouTrackIssueFields fields, string target, string? paneId) =>
        await _AskAsync<string>(YouTrackChannel.Start, new StartRequest(instance, issue, fields, target, paneId)) ?? string.Empty;

    public Task LinkAsync(string paneId, LinkedIssue link) =>
        _AskAsync<bool>(YouTrackChannel.Link, new LinkRequest(paneId, link));

    public Task UnlinkAsync(string paneId) =>
        _AskAsync<bool>(YouTrackChannel.Unlink, new PaneRequest(paneId));

    public Task<LinkedIssue?> LinkedAsync(string paneId) =>
        _AskAsync<LinkedIssue>(YouTrackChannel.Linked, new PaneRequest(paneId));

    // Raised on the backend's publishing thread, not the UI thread — a subscriber marshals before touching a control.
    public IDisposable OnLinkChanged(Action<string> paneChanged) =>
        channel.Subscribe(YouTrackChannel.LinkChanged, channelEvent =>
            paneChanged(channelEvent.Payload.Deserialize<PaneRequest>(YouTrackChannel.Json)?.PaneId ?? string.Empty));

    private async Task<T?> _AskAsync<T>(string action, object request)
    {
        var answer = await channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, YouTrackChannel.Json));
        return answer.Deserialize<T>(YouTrackChannel.Json);
    }
}
