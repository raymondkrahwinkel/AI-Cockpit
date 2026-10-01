using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

// J4, the assistant as Raymond's dispatcher: a question in its window is answered there, and the assistant puts a
// session on another profile out onto a desk through `cockpit-assistant-agents`, where that session then stands.
[Collection(JourneyCollection.Alone)]
public sealed class AssistantJourney
{
    private const string OtherProfile = "Other";

    [Fact]
    public async Task TheAssistantAnswersInItsWindow_AndPutsASessionOnAnotherProfileOntoTheDesk()
    {
        await using var cockpit = JourneyHost.Desktop();
        await _SetUpTheAssistantAsync(cockpit);
        await cockpit.StartDesktopAsync();

        // The chip, as the operator clicks it; the window it opens is the one the question goes into.
        var opened = new TaskCompletionSource<AssistantChatWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (Window.WindowOpenedEvent.AddClassHandler<AssistantChatWindow>((window, _) => opened.TrySetResult(window)))
        {
            HeadlessAvalonia.Run(() => cockpit.Services.GetRequiredService<AssistantIndicatorCoordinator>().Indicator.ClickCommand.Execute(null));
            await opened.Task.WaitAsync(Until.Ceiling);
        }

        var chat = HeadlessAvalonia.Run(() => opened.Task.Result.DataContext as AssistantChatViewModel ?? throw new InvalidOperationException("The assistant window has no conversation."));
        await HeadlessAvalonia.RunAsync(async () =>
        {
            chat.InputText = "what is open?";
            await chat.SendCommand.ExecuteAsync(null);
            var transcript = chat.Session?.Transcript ?? throw new InvalidOperationException("The assistant did not start.");
            await Until.CollectionHolds(transcript, () => transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == "echo: what is open?"));
        });

        var desk = HeadlessAvalonia.Run(() => cockpit.Cockpit.Workspaces.Settings.Workspaces.First(workspace => workspace.Type == WorkspaceType.Sessions).Id);
        var url = cockpit.Services.GetRequiredService<CockpitMcpEndpointHost>().GetServers().Single(server => server.Name == AssistantIdentity.ActMcpServerName).Url ?? "";
        await using var assistant = await cockpit.ConnectAsPaneAsync(AssistantIdentity.PaneId, AssistantIdentity.ActMcpServerName, url);
        var started = await JourneyHost.CallAsync(assistant, "start_agent", new() { ["workspaceId"] = desk, ["profile"] = OtherProfile, ["prompt"] = "go" });
        var paneId = started["paneId"]?.GetValue<string>() ?? "";
        var session = HeadlessAvalonia.Run(() => cockpit.Cockpit.Sessions.OfType<SessionViewModel>().Single(candidate => candidate.PaneId == paneId));
        await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == "echo: go")));

        Assert.True(started["ok"]?.GetValue<bool>(), started.ToJsonString());
        Assert.Equal((desk, true), HeadlessAvalonia.Run(() => (session.WorkspaceId, session.StartedByTheAssistant)));
        // AC-1450: the pane the registry made draws every row once, and the assistant's rail had it from the start (AC-1332).
        Assert.All(HeadlessAvalonia.Run(() => session.Transcript.GroupBy(row => (row.Kind, row.Text)).ToList()), group => Assert.Single(group));
        Assert.Contains(session, HeadlessAvalonia.Run(() => chat.SessionsStartedByTheAssistant.ToList()));
        Assert.Contains(cockpit.Drivers.Made, driver => driver.Profile?.Label == OtherProfile);
    }

    // Switched on, with an SDK profile of its own; and a second profile for it to put a session on.
    private static async Task _SetUpTheAssistantAsync(JourneyHost cockpit)
    {
        var services = cockpit.Services;
        SessionProfile Echo(string label) => new(label, new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Sdk };
        await services.GetRequiredService<ISessionProfileStore>().SaveAsync([Echo(OtherProfile)]);
        await services.GetRequiredService<IAssistantSettingsStore>().SaveAsync(new AssistantSettings { IsEnabled = true });
        await services.GetRequiredService<IAssistantProfileStore>().RepointAsync(Echo("Assistant"), replacesStandingInstruction: false);
    }
}
