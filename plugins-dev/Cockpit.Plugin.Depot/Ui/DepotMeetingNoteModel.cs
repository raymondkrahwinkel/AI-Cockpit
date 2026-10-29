using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-1579: what the "New meeting note" dialog asks of Depot. The real one goes to the backend part over the channel;
// the scenes and tests answer from fixtures.
internal interface IDepotMeetingNotes
{
    Task<DepotBrowserAnswer<DepotMeetingNoteOptions>> OptionsAsync();

    Task<DepotBrowserAnswer<DepotMeetingNoteProjectInfo>> ProjectAsync(string project);

    Task<DepotBrowserAnswer<DepotMeetingNoteCheck>> CheckAsync(string project, string? folder, string topic, DateOnly date);

    Task<DepotBrowserAnswer<DepotMeetingNoteCreated>> CreateAsync(
        string project, string topic, string? folder, string template, DateOnly date, IReadOnlyList<string> attendees);

    Task<DepotBrowserAnswer<string>> LinkAsync(string project, string path);
}

internal sealed class DepotMeetingNoteModel(ICockpitUiHost host, string connectionId) : IDepotMeetingNotes
{
    public Task<DepotBrowserAnswer<DepotMeetingNoteOptions>> OptionsAsync() =>
        _InvokeAsync<DepotMeetingNoteOptions>(DepotMeetingNoteChannel.Options, new(connectionId));

    public Task<DepotBrowserAnswer<DepotMeetingNoteProjectInfo>> ProjectAsync(string project) =>
        _InvokeAsync<DepotMeetingNoteProjectInfo>(DepotMeetingNoteChannel.Project, new(connectionId, project));

    public Task<DepotBrowserAnswer<DepotMeetingNoteCheck>> CheckAsync(string project, string? folder, string topic, DateOnly date) =>
        _InvokeAsync<DepotMeetingNoteCheck>(DepotMeetingNoteChannel.Check, new(connectionId, project, topic, folder, Date: _Day(date)));

    public Task<DepotBrowserAnswer<DepotMeetingNoteCreated>> CreateAsync(
        string project, string topic, string? folder, string template, DateOnly date, IReadOnlyList<string> attendees) =>
        _InvokeAsync<DepotMeetingNoteCreated>(DepotMeetingNoteChannel.Create, new(connectionId, project, topic, folder, template, attendees, Date: _Day(date)));

    public Task<DepotBrowserAnswer<string>> LinkAsync(string project, string path) =>
        _InvokeAsync<string>(DepotMeetingNoteChannel.Link, new(connectionId, project, Path: path));

    private static string _Day(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<DepotBrowserAnswer<T>> _InvokeAsync<T>(string action, DepotMeetingNoteRequest request)
    {
        try
        {
            var answer = await host.Channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, DepotChannel.Json)).ConfigureAwait(true);
            return answer.Deserialize<DepotBrowserAnswer<T>>(DepotChannel.Json)
                ?? DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, "Depot returned an empty answer.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, exception.Message);
        }
    }
}
