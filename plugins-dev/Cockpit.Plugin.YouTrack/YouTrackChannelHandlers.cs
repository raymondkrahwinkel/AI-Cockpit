using System.Text.Json;
using Cockpit.Plugin.YouTrack.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.YouTrack;

// AC-1397: what the backend part answers the UI part over the plugin's channel — every YouTrack call the dialog, the
// header and the picker make, and the session links they share. Each session-bound request names its pane (D6);
// nothing here asks the host which session is active.
internal sealed class YouTrackChannelHandlers(
    ICockpitHost host,
    SessionIssueLinks links,
    IssueStateChanges stateChanges)
{
    private readonly YouTrackClient _client = new();

    public IReadOnlyList<IDisposable> Register(IPluginBackendChannel channel)
    {
        var workflow = new YouTrackWorkflow(_client);

        // The header in each session re-reads its link when this says that pane's link moved.
        links.Changed += (_, paneId) =>
            channel.Publish(YouTrackChannel.LinkChanged, JsonSerializer.SerializeToElement(new PaneRequest(paneId), YouTrackChannel.Json));

        return
        [
            _Handle<InstanceRequest, IReadOnlyList<YouTrackProject>>(channel, YouTrackChannel.Projects, (request, cancellationToken) =>
                _client.GetProjectsAsync(request.Instance.InstanceUrl, request.Instance.Token, cancellationToken)),
            _Handle<PreferredTagsRequest, IReadOnlyList<string>>(channel, YouTrackChannel.PreferredTags, (request, cancellationToken) =>
                YouTrackProjectField.ResolvePreferredTagsAsync(host, request.PaneId, request.DefaultProjectTag, cancellationToken)),
            _Handle<IssuesRequest, IReadOnlyList<YouTrackIssue>>(channel, YouTrackChannel.Issues, (request, cancellationToken) =>
                _client.GetOpenIssuesAsync(request.Instance.InstanceUrl, request.Instance.Token, request.ProjectTags, request.Filter, request.AssignedToMe, request.Top, cancellationToken)),
            _Handle<ProjectStateFieldRequest, ProjectStateFieldAnswer>(channel, YouTrackChannel.ProjectStateField, async (request, cancellationToken) =>
            {
                var (fieldName, values) = await _client.GetProjectStateFieldAsync(request.Instance.InstanceUrl, request.Instance.Token, request.ProjectShortName, cancellationToken);
                return new ProjectStateFieldAnswer(fieldName, values);
            }),
            _Handle<IssueRequest, YouTrackIssueFields>(channel, YouTrackChannel.IssueFields, (request, cancellationToken) =>
                _client.GetIssueFieldsAsync(request.Instance.InstanceUrl, request.Instance.Token, request.Issue, cancellationToken)),
            _Handle<SetStateRequest, bool>(channel, YouTrackChannel.SetState, async (request, cancellationToken) =>
            {
                await _client.SetStateAsync(request.Instance.InstanceUrl, request.Instance.Token, request.Issue, request.State, request.Target, cancellationToken);
                stateChanges.Moved(request.Instance, request.Issue, request.State.CurrentValue ?? string.Empty, request.Target, _DirectoryOf(request.PaneId));
                return true;
            }),
            _Handle<StartRequest, string>(channel, YouTrackChannel.Start, async (request, cancellationToken) =>
            {
                var result = await workflow.StartAsync(request.Instance, request.Issue, request.Fields, request.Target, cancellationToken);
                stateChanges.Moved(request.Instance, request.Issue, request.Fields.State?.CurrentValue ?? string.Empty, request.Target, _DirectoryOf(request.PaneId));
                return result;
            }),
            _Handle<LinkRequest, bool>(channel, YouTrackChannel.Link, (request, _) =>
            {
                links.Link(request.PaneId, request.Link, _DirectoryOf(request.PaneId));
                return Task.FromResult(true);
            }),
            _Handle<PaneRequest, bool>(channel, YouTrackChannel.Unlink, (request, _) =>
            {
                links.Unlink(request.PaneId);
                return Task.FromResult(true);
            }),
            _Handle<PaneRequest, LinkedIssue?>(channel, YouTrackChannel.Linked, (request, _) =>
                Task.FromResult(links.For(request.PaneId))),
        ];
    }

    // Where the pane a request names works — what a flow triggered by the move or the link is handed to run in.
    private string? _DirectoryOf(string? paneId) =>
        string.IsNullOrEmpty(paneId) ? null : host.Sessions.GetWorkingDirectory(paneId);

    private static IDisposable _Handle<TRequest, TAnswer>(
        IPluginBackendChannel channel,
        string action,
        Func<TRequest, CancellationToken, Task<TAnswer>> answer) =>
        channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<TRequest>(YouTrackChannel.Json)
                ?? throw new ArgumentException($"The '{action}' request carried no payload.", nameof(payload));
            return JsonSerializer.SerializeToElement(await answer(request, cancellationToken), YouTrackChannel.Json);
        });
}
