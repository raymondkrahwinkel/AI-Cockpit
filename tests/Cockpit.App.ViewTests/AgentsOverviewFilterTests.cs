using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Projects;
using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class AgentsOverviewFilterTests
{
    // Layer 3: the real panel model combines filters while the sidebar keeps its own state.
    [Fact]
    public void FiltersCombineAndFollowLiveChangesIndependentlyOfSidebar()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = new CockpitViewModel();
            cockpit.Sessions.Clear();
            cockpit.Projects.Projects.Add(new Project("target", "Target"));
            var match = new SessionViewModel
            {
                Title = "Release", Statusline = "review the parser", ProjectId = "target",
                SessionStatus = SessionStatus.NeedsAttention,
            };
            cockpit.Sessions.Add(match);
            cockpit.Sessions.Add(new SessionViewModel { Title = "Release", ProjectId = "target", SessionStatus = SessionStatus.Busy });
            cockpit.Sessions.Add(new SessionViewModel { Title = "Release", SessionStatus = SessionStatus.NeedsAttention });
            var agents = cockpit.AgentsOverview;
            agents.ShowOnlyWaiting = true;
            agents.SelectedMachine = "here";
            agents.SelectedProject = "Target";
            agents.SearchText = "PARSER";

            Assert.Single(agents.Columns.SelectMany(column => column.Cards));
            Assert.Equal("Release", agents.Columns[1].Cards.Single().Title);
            Assert.Equal(2, agents.WaitingCount);
            Assert.False(cockpit.ShowOnlyWaiting);
            agents.SelectedMachine = "another machine";
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));
            agents.SelectedMachine = "here";
            match.SessionStatus = SessionStatus.Done;
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));

            agents.ShowOnlyWaiting = false;
            agents.SelectedProject = "All projects";
            agents.SearchText = "old task";
            var oldTask = new DelegatedTaskRowViewModel(new DelegatedTaskView(
                "old", "Coder", "old task", null, DelegatedTaskStatus.Completed,
                DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2),
                1, "Finished", null));
            cockpit.DelegatedTasks.Tasks.Add(oldTask);
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));
            agents.ShowDelegatedTasks = true;
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));
            Assert.True(agents.ShowsMore);
            agents.ShowMoreCommand.Execute(null);
            var taskCard = Assert.Single(agents.Columns[2].Cards);
            Assert.True(taskCard.IsDelegatedTask);
            taskCard.OpenCommand.Execute(null);
            Assert.Same(oldTask, cockpit.DelegatedTasks.SelectedTask);
            agents.ShowDelegatedTasks = false;
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));

            match.RestoreOffer = new SessionRestorePlan(new WorkspacePane(match.PaneId, PaneKind.AiSession),
                null, SessionRestoreAvailability.Unknown, "Saved session")
            {
                State = new SessionStateRecord(match.PaneId, null, null, null, SessionConversationIdState.Unknown,
                    null, null, null, null, DateTimeOffset.UtcNow.AddDays(-2)),
            };
            agents.ShowOlder = false;
            agents.SearchText = "parser";
            Assert.Empty(agents.Columns.SelectMany(column => column.Cards));
            agents.ShowMoreCommand.Execute(null);
            Assert.Equal("Release", Assert.Single(agents.Columns[2].Cards).Title);
        });
    }
}
