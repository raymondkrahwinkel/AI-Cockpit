using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-248: the "Shared via {source}" groups AC-245 built for the separate Manage-projects dialog now also render on
/// the default landing screen — before this, a project shared through a plugin was invisible unless that dialog was
/// opened, so someone who never opened it saw no sign a shared source existed. Measured against the real markup,
/// same reasoning as <see cref="ProjectsWorkspaceCategoryGroupingTests"/>: the fix's own nesting (three
/// <c>ItemsControl</c>s deep for the card, one more than the local-project cards) is exactly where a
/// <c>$parent[ItemsControl]</c> binding would silently land on the wrong ancestor.
/// </summary>
[Collection("avalonia")]
public class ProjectsWorkspaceSharedProjectsTests
{
    [Fact]
    public async Task ASharedProject_RendersItsCard_AndFinishSettingUpReachesTheRealCommand() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var source = Substitute.For<ISharedProjectSource>();
            source.Key.Returns("depot");
            source.SourceName.Returns("Depot — Work");
            var sharedProject = new SharedProject("depot:onboarding", "Onboarding flow") { Description = "New-hire checklist.", Role = "Editor" };
            source.ListAsync(Arg.Any<CancellationToken>()).Returns(SharedProjectListResult.Success([sharedProject]));

            var registry = Substitute.For<ISharedProjectSourceRegistry>();
            registry.Sources.Returns([source]);

            var store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty);
            var dialogs = Substitute.For<ISessionDialogService>();
            var projects = TestProjects.ViewModel(store, dialogs, sharedSources: registry);
            await projects.LoadAsync();
            await projects.SharedProjectsLoadTask;

            var cockpit = _NewCockpit(dialogs, projects);
            await cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();

            // AC-1600: the team project is a row in its source's section, and its Add… reaches the real command.
            var heading = view.GetVisualDescendants().OfType<ProjectSectionHeader>()
                .FirstOrDefault(header => header.DataContext is ProjectSectionViewModel { Title: "◆ Depot — Work" });
            var cardName = view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Text == "Onboarding flow");
            var finishSettingUpButton = view.GetVisualDescendants().OfType<ProjectListRow>().Single()
                .GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => Equals(button.Content, "Add…"));

            Assert.NotNull(heading);
            Assert.NotNull(cardName);
            Assert.NotNull(finishSettingUpButton);
            Assert.Same(cockpit.Projects.FinishSettingUpCommand, finishSettingUpButton.Command);
            Assert.Equal(sharedProject.Id, ((SharedProjectOffer?)finishSettingUpButton.CommandParameter)?.Id);

            window.Close();
        });

    // Both directions in one test: shown with no source registered at all, hidden once one is — a signed-out
    // connection already speaks for itself in its own section, so the line must not also claim nothing is set up.
    [Fact]
    public async Task ThePointerLine_ShowsOnlyWhileNoSharedProjectSourceIsRegistered() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            foreach (var registered in new[] { false, true })
            {
                ISharedProjectSourceRegistry? registry = null;
                if (registered)
                {
                    var source = Substitute.For<ISharedProjectSource>();
                    source.Key.Returns("depot");
                    source.SourceName.Returns("Depot — Work");
                    source.ListAsync(Arg.Any<CancellationToken>()).Returns(SharedProjectListResult.Failed("Sign in to this Depot connection to see its shared projects."));
                    registry = Substitute.For<ISharedProjectSourceRegistry>();
                    registry.Sources.Returns([source]);
                }

                var store = Substitute.For<IProjectStore>();
                store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty);
                var dialogs = Substitute.For<ISessionDialogService>();
                var projects = TestProjects.ViewModel(store, dialogs, sharedSources: registry);
                await projects.LoadAsync();
                await projects.SharedProjectsLoadTask;

                var cockpit = _NewCockpit(dialogs, projects);
                await cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

                var view = new CockpitView { DataContext = cockpit };
                var window = new Window { Content = view, Width = 1100, Height = 800 };
                window.Show();
                window.UpdateLayout();

                // Still in the visual tree either way (a hidden control is not torn down), so this reads IsEffectivelyVisible.
                var pointer = view.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => _RunText(text).Contains("Projects can also come from a plugin"));

                Assert.NotNull(pointer);
                Assert.Equal(!registered, pointer.IsEffectivelyVisible);

                window.Close();
            }
        });

    // The pointer line mixes plain text with <Run> spans (for the "Options" bold word) — Avalonia turns the plain
    // text into an implicit Run too, so concatenating every Run's own Text is what actually reads the sentence back,
    // same idiom MarkdownViewFilePathTests already uses for a Run-carrying TextBlock.
    private static string _RunText(TextBlock text) => string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    private static CockpitViewModel _NewCockpit(ISessionDialogService dialogs, ProjectsViewModel projects)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            projects: projects);
    }
}
