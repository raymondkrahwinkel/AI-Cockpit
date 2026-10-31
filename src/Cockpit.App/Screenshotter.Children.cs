using Avalonia.Controls;
using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _ChildHistoryPopout() => _ChildHistoryScene("sidebar");
    private static Window _ChildHistoryScene(string state)
    {
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true, SidebarWidth = 260 };
        var owners = cockpit.Sessions.OfType<SessionViewModel>().Take(2).ToArray();
        foreach (var owner in owners)
        {
            var running = new BackgroundTask("running-id", BackgroundTaskKind.SubAgent, "Review the parser change")
            {
                AgentId = "running-id", RunId = owner.PaneId, LastTool = "Grep", Tokens = 18400,
            };
            owner.Apply(new BackgroundTasksChanged
            {
                SessionId = owner.PaneId,
                Tasks = [running],
                Children = [running, new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Draft the release notes")
                {
                    AgentId = "same-id", RunId = owner.PaneId, State = "completed", LastTool = "Write", Tokens = 31200,
                }],
            });
        }
        if (owners.FirstOrDefault() is { } selected && state != "sidebar")
        {
            selected.ChildAgents[1].OpenCommand.Execute(null);
            if (selected.ChildTranscript is { } transcript)
            {
                transcript.Status = state == "unavailable" ? "Not available: owner connection has stopped" : state == "empty" ? "No entries" : "";
                if (state == "finished")
                {
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "Review the session driver and report your findings."));
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolResult, "Read · src/Sessions/Driver.cs\nThe driver routes child events by owner and run."));
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "The review is complete. Child events remain isolated from the parent transcript."));
                }
            }
        }
        return new MainWindow { DataContext = cockpit };
    }
}
