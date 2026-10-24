using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

// J4, the assistant as Raymond's dispatcher: a question in its window is answered there, it puts sessions out onto a
// desk through `cockpit-assistant-agents`, and (AC-1616) its conversation reads per project: a prompt and a session's
// message each land in their project's thread, Questions holds the open question, a closed thread reopens by itself.
[Collection(JourneyCollection.Alone)]
public sealed class AssistantJourney
{
    private const string OtherProfile = "Other";

    private const string AgentsServer = "cockpit-agents";

    [Fact]
    public async Task TheAssistantAnswersInItsWindow_AndPutsASessionOnAnotherProfileOntoTheDesk()
    {
        await using var cockpit = JourneyHost.Desktop();
        var (depot, waymark) = await _SetUpTheAssistantAsync(cockpit);
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
        var started = await JourneyHost.CallAsync(assistant, "start_agent", new() { ["workspaceId"] = desk, ["profile"] = OtherProfile, ["projectId"] = depot.Id, ["prompt"] = "go" });
        var paneId = started["paneId"]?.GetValue<string>() ?? "";
        var session = HeadlessAvalonia.Run(() => cockpit.Cockpit.Sessions.OfType<SessionViewModel>().Single(candidate => candidate.PaneId == paneId));
        await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == "echo: go")));

        Assert.True(started["ok"]?.GetValue<bool>(), started.ToJsonString());
        Assert.Equal((desk, true), HeadlessAvalonia.Run(() => (session.WorkspaceId, session.StartedByTheAssistant)));
        // AC-1439: the launcher's session lands as one pane drawing every row exactly once — the brief and its answer
        // there, nothing twice — and the assistant's rail had it from the start (AC-1332).
        var rows = HeadlessAvalonia.Run(() => session.Transcript.Select(row => (row.Kind, row.Text)).ToList());
        Assert.Single(rows, row => row == (TranscriptEntryKind.UserText, "go"));
        Assert.Single(rows, row => row == (TranscriptEntryKind.AssistantText, "echo: go"));
        Assert.All(rows.GroupBy(row => row), group => Assert.Single(group));
        Assert.Contains(session, HeadlessAvalonia.Run(() => chat.SessionsStartedByTheAssistant.ToList()));
        Assert.Contains(cockpit.Drivers.Made, driver => driver.Profile?.Label == OtherProfile);

        // AC-1616: a session for WayMark tells the assistant something; it rides along with the operator's next turn.
        var other = await JourneyHost.CallAsync(assistant, "start_agent", new() { ["workspaceId"] = desk, ["profile"] = OtherProfile, ["projectId"] = waymark.Id, ["prompt"] = "go" });
        var agentsUrl = cockpit.Services.GetRequiredService<CockpitMcpEndpointHost>().GetServers().Single(server => server.Name == AgentsServer).Url ?? "";
        await using var waymarkPane = await cockpit.ConnectAsPaneAsync(other["paneId"]?.GetValue<string>() ?? "", AgentsServer, agentsUrl);
        await JourneyHost.CallAsync(waymarkPane, "notify", new() { ["toPaneId"] = AssistantIdentity.PaneId, ["kind"] = "heads-up", ["body"] = "WM-12 is green" });

        // The operator asks in Depot's thread, the one the rail has for the session on Depot.
        var transcript = HeadlessAvalonia.Run(() => chat.Session?.Transcript ?? throw new InvalidOperationException("The assistant has no conversation."));
        await HeadlessAvalonia.RunAsync(async () =>
        {
            await Until.CollectionHolds(chat.ThreadItems, () => chat.ThreadItems.Any(item => item.ProjectId == depot.Id));
            chat.SelectedThreadItem = chat.ThreadItems.Single(item => item.ProjectId == depot.Id);
            chat.InputText = "and Depot?";
            await chat.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(transcript, () => transcript.Any(row => row.IsInboxRow && row.Text.Contains("WM-12", StringComparison.Ordinal))
                && transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text.EndsWith("and Depot?", StringComparison.Ordinal)));
        });

        var (reply, mail) = HeadlessAvalonia.Run(() => (
            transcript.Last(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text.EndsWith("and Depot?", StringComparison.Ordinal)).ThreadProjectIds.ToList(),
            transcript.Single(row => row.IsInboxRow && row.Text.Contains("WM-12", StringComparison.Ordinal)).ThreadProjectIds.ToList()));
        // AC-1646: the echo answers with the rode-along "WM-12" too, so the turn names WayMark as well; it is added to
        // Depot's thread, where the question was typed, instead of taking the reply away from it.
        Assert.Equal([waymark.Id, depot.Id], reply);
        Assert.Equal([waymark.Id], mail);

        // A question about Depot waits in Questions for you, and is what that page shows.
        await JourneyHost.CallAsync(assistant, "ask_structured_question", new()
        {
            ["question"] = "Ship the Depot route?",
            ["options"] = new[] { new { label = "Yes" }, new { label = "Not yet" } },
            ["projectIds"] = new[] { depot.Id },
        });
        await HeadlessAvalonia.RunAsync(() => Until.Holds(chat, () => chat.OpenQuestionCount == 1));
        var (onQuestions, waiting) = HeadlessAvalonia.Run(() =>
        {
            chat.SelectedThreadItem = chat.ThreadItems.Single(item => item.Kind == AssistantThreadItemKind.Questions);
            return (chat.IsQuestionsPage, chat.Session?.OpenQuestions.Select(row => row.Text).ToList() ?? []);
        });
        Assert.True(onQuestions);
        Assert.Equal(["Ship the Depot route?"], waiting);

        // AC-1653: the card carries its own Send once an option is picked, and the answer comes back as a message.
        var card = HeadlessAvalonia.Run(() => chat.Session!.OpenQuestions.Single());
        var canSend = HeadlessAvalonia.Run(() =>
        {
            card.QuestionPrompts![0].Options[0].SelectCommand.Execute(null);
            return card.IsAwaitingAnswer && card.CanSubmitAnswers;
        });
        Assert.True(canSend);
        await HeadlessAvalonia.RunAsync(async () =>
        {
            await chat.Session!.SubmitQuestionAnswersCommand.ExecuteAsync(card);
            await Until.CollectionHolds(transcript, () => transcript.Any(row => row.Kind == TranscriptEntryKind.UserText && row.Text.Contains("Yes", StringComparison.Ordinal)));
        });
        Assert.False(HeadlessAvalonia.Run(() => card.IsAwaitingAnswer));

        // WayMark's thread closed by the operator, and back by itself once WayMark has something new.
        var closed = HeadlessAvalonia.Run(() =>
        {
            chat.CloseThreadCommand.Execute(chat.ThreadItems.Single(item => item.ProjectId == waymark.Id));
            return chat.ClosedThreadItems.Select(item => item.ProjectId).ToList();
        });
        await JourneyHost.CallAsync(waymarkPane, "notify", new() { ["toPaneId"] = AssistantIdentity.PaneId, ["kind"] = "heads-up", ["body"] = "WM-13 needs a look" });
        await HeadlessAvalonia.RunAsync(async () =>
        {
            chat.SelectedThreadItem = chat.ThreadItems.Single(item => item.Kind == AssistantThreadItemKind.All);
            chat.InputText = "anything else?";
            await chat.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(chat.ThreadItems, () => chat.ThreadItems.Any(item => item.ProjectId == waymark.Id));
        });

        Assert.Equal([waymark.Id], closed);
        Assert.Empty(HeadlessAvalonia.Run(() => chat.ClosedThreadItems.ToList()));
    }

    // Switched on, with an SDK profile of its own; a second profile for it to put a session on; and two projects.
    private static async Task<(Project Depot, Project WayMark)> _SetUpTheAssistantAsync(JourneyHost cockpit)
    {
        var services = cockpit.Services;
        SessionProfile Echo(string label) => new(label, new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Sdk };
        await services.GetRequiredService<ISessionProfileStore>().SaveAsync([Echo(OtherProfile)]);
        await services.GetRequiredService<IAssistantSettingsStore>().SaveAsync(new AssistantSettings { IsEnabled = true });
        await services.GetRequiredService<IAssistantProfileStore>().RepointAsync(Echo("Assistant"), replacesStandingInstruction: false);
        var projects = services.GetRequiredService<IProjectEditor>();
        return (await projects.AddNewProjectAsync(Project.Create("Depot")), await projects.AddNewProjectAsync(Project.Create("WayMark")));
    }
}
