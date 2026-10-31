using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private sealed class ChildSteeringOwner : SessionViewModel
    {
        private bool _ready = true;
        public override bool IsSessionReady => _ready;
        public void StopForFixture()
        {
            _ready = false;
            OnPropertyChanged(nameof(IsSessionReady));
        }
    }

    private sealed record SteeringFixture(CockpitViewModel Cockpit, SessionPanelViewModel Owner,
        SessionViewModel Sibling, ChildTranscriptViewModel Page);

    private static Window _ChildSteeringScene(bool terminal = false)
    {
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true, SidebarWidth = 260 };
        SessionPanelViewModel owner = terminal
            ? new TtyViewModel { Title = "atlas · original terminal", PromptSink = _ => throw new InvalidOperationException("A navigation action must never type into the TTY.") }
            : new ChildSteeringOwner { Title = "atlas · parent", InputText = "Keep my existing draft.", SessionStatus = SessionStatus.Busy };
        var sibling = new ChildSteeringOwner { Title = "harbor · other parent", InputText = "Keep the other parent's draft." };
        cockpit.Sessions.Clear();
        cockpit.Sessions.Add(owner);
        cockpit.Sessions.Add(sibling);
        cockpit.SelectSessionCommand.Execute(owner);
        foreach (var parent in new[] { owner, sibling })
        {
            parent.UpdateChildAgents([new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Review the parser change")
            {
                AgentId = "same-id", RunId = parent.PaneId, SourceRevision = "source-1", AgentType = "general-purpose",
            }]);
        }
        if (owner is SessionViewModel sdk)
        {
            sdk.QueuedMessages.Clear();
            sdk.QueuedMessages.Add(new QueuedMessageViewModel(new QueuedPrompt("Existing queued request", []), null, _ => { }));
        }
        owner.ChildAgents[0].OpenCommand.Execute(null);
        ChildTranscriptViewModel page = owner.ChildTranscript ?? throw new InvalidOperationException("The fixture must open its child.");
        page.Status = "";
        page.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "Review the parser change and report the result to the parent."));
        page.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "The parser review is in progress. This is fixture content; no provider turn was started."));
        var window = new MainWindow { DataContext = cockpit, Tag = new SteeringFixture(cockpit, owner, sibling, page) };
        window.DataTemplates.Add(new FuncDataTemplate<ChildSteeringOwner>((value, _) => new SessionView { DataContext = value }));
        return window;
    }

    private static void _ExerciseChildSteering(Window window, string action)
    {
        var fixture = window.Tag as SteeringFixture ?? throw new InvalidOperationException("Missing steering fixture.");
        SessionPanelViewModel owner = fixture.Owner;
        if (action == "stopped" && owner is ChildSteeringOwner stopped)
        {
            stopped.StopForFixture();
        }
        if (action == "stale")
        {
            owner.UpdateChildAgents([owner.ChildAgents[0].Reference with { RunId = "next-run", SourceRevision = "source-2" }]);
        }
        if (action == "terminal")
        {
            fixture.Page.OpenOriginalTerminalCommand.Execute(null);
            if (owner.IsReadingChild || !ReferenceEquals(fixture.Cockpit.SelectedSession, owner) || fixture.Cockpit.Sessions.Count != 2)
            {
                throw new InvalidOperationException("Terminal navigation must return to the existing owner without starting a pane.");
            }
            return;
        }
        fixture.Page.PrepareParentRequestCommand.Execute(null);
        var sdk = owner as SessionViewModel ?? throw new InvalidOperationException("Parent drafts belong to SDK fixtures.");
        if (action == "prepared")
        {
            if (owner.IsReadingChild || !sdk.InputText.StartsWith("Keep my existing draft.\n\n", StringComparison.Ordinal)
                || !sdk.InputText.Contains("same-id", StringComparison.Ordinal) || !sdk.InputText.Contains(owner.PaneId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Preparing a request must preserve the draft and target the selected owner and run.");
            }
        }
        else if (sdk.InputText != "Keep my existing draft.")
        {
            throw new InvalidOperationException("A stopped owner or stale child may not change the draft.");
        }
        if (fixture.Sibling.InputText != "Keep the other parent's draft." || sdk.QueuedMessages.Count != 1)
        {
            throw new InvalidOperationException($"A draft action must leave the other owner and queue alone (sibling preserved: {fixture.Sibling.InputText == "Keep the other parent's draft."}, queue: {sdk.QueuedMessages.Count}).");
        }
    }
}
