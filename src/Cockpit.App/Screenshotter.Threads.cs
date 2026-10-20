using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

// Assistant threads: one conversation across Cockpit, Depot, WayMark and General, staged at the rail's three widths.
internal static partial class Screenshotter
{
    private static readonly Dictionary<string, string> ThreadSceneProjects = new(StringComparer.Ordinal)
    {
        ["cockpit"] = "Cockpit",
        ["depot"] = "Depot",
        ["waymark"] = "WayMark",
    };

    private static AssistantChatWindow _AssistantThreads(double width, string? thread = null, bool questions = false, bool overlay = false)
    {
        var chat = _AssistantChatViewModel(_AssistantThreadsConversation());
        var window = new AssistantChatWindow
        {
            DataContext = chat,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = width,
            Height = 720,
        };

        chat.ViewWidth = width;
        chat.SelectedThreadItem = questions
            ? chat.ThreadItems[0]
            : chat.ThreadItems.FirstOrDefault(item => item.ThreadKey == thread && item.Kind != AssistantThreadItemKind.Questions);
        chat.IsRailOverlayOpen = overlay;
        return window;
    }

    // Docked in the rail at a given width: 600 has labels, 400 icons, 280 the picker. Null leaves the rail collapsed.
    private static Window _AssistantThreadsDocked(double? railWidth)
    {
        var conversation = _AssistantThreadsConversation();
        var panels = new Docking.DockPanelRegistry();
        panels.Register(new Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration(
            Services.AssistantIndicatorCoordinator.DockPanelId,
            "Assistant",
            Material.Icons.MaterialIconKind.Creation,
            () =>
            {
                var chat = _AssistantChatViewModel(conversation);
                chat.IsDocked = true;
                return new AssistantChatView { DataContext = chat };
            }));

        var cockpit = new CockpitViewModel(dockPanelRegistry: panels)
        {
            OpenDockPanelId = railWidth is null ? null : Services.AssistantIndicatorCoordinator.DockPanelId,
            AssistantDocked = true,
            DockRailWidth = railWidth ?? Cockpit.Core.Layout.LayoutSettings.DefaultDockRailWidth,
        };

        return new MainWindow { DataContext = cockpit };
    }

    internal static SessionViewModel _AssistantThreadsConversation()
    {
        var session = new SessionViewModel
        {
            Title = "Assistant",
            Threads = new ThreadLookup(id => ThreadSceneProjects.GetValueOrDefault(id), _ => null, _ => null),
        };

        session.Transcript.Add(_Prompt("Waarom synct Strava niet meer?", "waymark"));
        session.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "Het token van Strava is verlopen sinds zaterdag. Opnieuw koppelen lost het op."));

        session.Transcript.Add(_Message(TranscriptEntryKind.Status, "AC-1593 org policy", "done", "local gate green (89/92, 3 known)", "cockpit"));
        session.Transcript.Add(_Message(TranscriptEntryKind.Status, "AC-1588", "closed", "session closed", "cockpit"));
        session.Transcript.Add(_Message(TranscriptEntryKind.Inbox, "DEP-422", "heads-up",
            "De route staat en ik schrijf nu de test voor de ETag. Nog geen PR.", "depot"));
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Ik draai de Depot-tests even."));
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, string.Empty)
        {
            ToolName = "Bash",
            ToolUseId = "scene-bash",
            InputJson = """{"command":"dotnet test tests/Depot.Api.Tests --filter CockpitProjects"}""",
            IsPendingPermission = true,
        });
        session.Transcript.Add(_Question("Mag ?org= ontbreken, of is hij verplicht zodra iemand lid is van meer dan één org?",
            ["Optioneel", "Verplicht bij 2+ orgs"], "depot"));

        session.Transcript.Add(_Prompt("Zet donderdag 14:00 tandarts in mijn agenda, reminder de dag ervoor.", project: null));
        session.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "Staat erin: donderdag 22 oktober 14:00, tandarts, met een reminder op woensdag."));

        session.Transcript.Add(_Question("Moet de never-send-regel ook gelden voor de shell-terminal van de operator?",
            ["Nee, alleen agents", "Ja, ook de shell"], "cockpit"));
        return session;
    }

    private static TranscriptEntryViewModel _Prompt(string text, string? project) => new(TranscriptEntryKind.UserText, text)
    {
        ProjectIds = project is null ? [] : [project],
        ProjectSources = project is null ? null : new Dictionary<string, ProjectSource> { [project] = ProjectSource.Explicit },
    };

    private static TranscriptEntryViewModel _Message(TranscriptEntryKind kind, string sender, string messageKind, string body, string project) =>
        new(kind, body)
        {
            SenderPaneId = "scene-" + sender,
            SenderTitle = sender,
            MessageKind = messageKind,
            ProjectIds = [project],
            ProjectSources = new Dictionary<string, ProjectSource> { [project] = ProjectSource.Session },
        };

    private static TranscriptEntryViewModel _Question(string question, string[] options, string project)
    {
        var input = System.Text.Json.JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new { question, header = "Question", multiSelect = false, options = options.Select(label => new { label }).ToArray() },
            },
        });

        return new TranscriptEntryViewModel(TranscriptEntryKind.Question, question)
        {
            InputJson = input,
            QuestionPrompts = AskUserQuestionViewModel.Parse(input),
            IsPendingBrokerAnswer = true,
            ProjectIds = [project],
            ProjectSources = new Dictionary<string, ProjectSource> { [project] = ProjectSource.AssistantTag },
        };
    }
}
