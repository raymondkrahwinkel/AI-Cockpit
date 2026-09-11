using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1304: the Simple stand's start screen — "which project do you want to work on". What it must not become is
/// a second projects list beside the one the panels stand already has, which is why the second test here holds the
/// rendered cards against the very objects the overview draws rather than against their contents.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1304StartScreenTests
{
    // Criterion 1(a). All three doors in one test, because one screen reached three ways is the whole claim: a
    // screen built per entrance passes each door on its own and fails exactly here.
    [Fact]
    public void ThreeDoors_AllLandOnTheSameStartScreen()
    {
        // The parameterless constructor seeds the previewer's transcript; a first start has nothing said in it.
        var assistant = new SessionViewModel { Title = "Assistant" };
        assistant.Transcript.Clear();
        var cockpit = _SimpleStand(withAssistant: true, assistant);
        var agent = cockpit.Sessions[0];

        // Door 1: a first start with nothing picked and nothing said yet. AC-1316: the screen is the empty state
        // of the assistant's own conversation, so it shows in that conversation rather than in front of it — and
        // the first thing said is what takes it away.
        Assert.True(cockpit.SimpleStandShowsTheStartScreen);
        Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        assistant.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "hi"));
        Assert.False(cockpit.SimpleStandShowsTheStartScreen);

        // Door 2: "+ New session" while a conversation stands. It has to take the column off whatever held it,
        // or the question would be asked over the answer.
        cockpit.SimpleSelectedSession = agent;
        Assert.False(cockpit.SimpleStandShowsTheStartScreen);

        cockpit.ShowSimpleStartScreenCommand.Execute(null);

        Assert.True(cockpit.SimpleStandShowsTheStartScreen);
        Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.Null(cockpit.SimpleSelectedSession);

        // Door 3: closing the last conversation. Nothing used to clear this stand's own selection, so the column
        // went on pointing at a session that no longer existed.
        cockpit.SimpleSelectedSession = agent;
        Assert.False(cockpit.SimpleStandShowsTheStartScreen);
        cockpit.AssistantChat = null;

        cockpit.CloseSessionCommand.Execute(agent);

        Assert.Null(cockpit.SimpleSelectedSession);
        Assert.True(cockpit.SimpleStandShowsTheStartScreen);
    }

    // Criterion 1(b) and 3(a), against the real markup: the cards must be the overview's own card objects — a list
    // built for this screen would be equal in content and a different set of instances — and the job button must
    // reach the no-dialog command. Avalonia logs a binding landing on nothing, so that one is asserted by identity.
    [Fact]
    public void TheStartScreensCards_AreTheOverviewsOwnCards_AndAJobReachesTheNoDialogCommand() =>
        HeadlessAvalonia.Run(() =>
        {
            var job = new ProjectJob("Process this month's invoices", "changes nothing · reports only");
            var invoices = Project.Create("Invoices") with
            {
                Category = "Werk",
                DefaultProfileLabel = "work",
                Jobs = [job],
            };
            var store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [invoices] });

            var dialogs = Substitute.For<ISessionDialogService>();
            var projects = new ProjectsViewModel(store, dialogs);
            projects.LoadAsync().GetAwaiter().GetResult();

            var cockpit = _Cockpit(dialogs, projects);
            cockpit.SimpleView = true;
            cockpit.Sessions.Clear();

            // AC-1316: the cards are drawn inside the assistant's own conversation, which the column builds from
            // this factory — the coordinator's shape, with a session that has not started yet.
            var chat = _AssistantChat(cockpit, session: null);
            cockpit.CreateSimpleViewChatView = () =>
            {
                chat.IsSimpleViewHost = true;
                return new AssistantChatView { DataContext = chat };
            };

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 1100, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var drawn = view.GetVisualDescendants().OfType<ProjectCardView>().ToList();
            var fromTheOverview = cockpit.Projects.ProjectCategoryGroups.SelectMany(group => group.Cards).ToList();
            Assert.Equal(fromTheOverview, drawn.Select(card => card.DataContext));

            var jobButton = drawn[0].GetVisualDescendants().OfType<Button>()
                .First(button => button.Content is StackPanel panel
                    && panel.Children.OfType<TextBlock>().Any(text => text.Text == job.Prompt));

            Assert.Same(cockpit.StartProjectJobNowCommand, jobButton.Command);
            Assert.Same(job, ((ProjectJobChoice?)jobButton.CommandParameter)?.Job);

            window.Close();
        });

    private static AssistantChatViewModel _AssistantChat(CockpitViewModel cockpit, SessionViewModel? session)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        return new AssistantChatViewModel(host, settings, Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);
    }

    // The parameterless constructor is the previewer's and seeds sample sessions; a real cockpit starts empty.
    private static CockpitViewModel _SimpleStand(bool withAssistant, SessionViewModel? assistant = null)
    {
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        cockpit.Sessions.Add(new SessionViewModel { Title = "Kind-cluster als staging-evenbeeld" });
        cockpit.AssistantChat = _AssistantChat(cockpit, withAssistant ? assistant ?? new SessionViewModel { Title = "Assistant" } : null);

        return cockpit;
    }

    private static CockpitViewModel _Cockpit(ISessionDialogService dialogs, ProjectsViewModel projects)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcripts = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcripts.LoadAsync().Returns(new TranscriptDisplaySettings());
        var behavior = Substitute.For<ISessionBehaviorSettingsStore>();
        behavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminals = Substitute.For<ITerminalSettingsStore>();
        terminals.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcripts,
            behavior,
            layout,
            voice,
            terminals,
            projects: projects);
    }
}
