using Avalonia.Controls;
using Avalonia.VisualTree;
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
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-655: the "What do you want to work on?" workspace picker used to list every project flat
/// (<c>ProjectsViewModel.RecentProjects</c>) instead of grouped by category the way the Manage-projects dialog
/// (AC-618) already does. Measured against the real markup, not the view model alone: the fix's own nesting —
/// a category ItemsControl wrapping a per-category cards ItemsControl — hit the exact <c>$parent[ItemsControl]</c>
/// trap <c>ProjectsDialog.axaml</c> already had to work around (see that file's own comment), so a Start click has
/// to actually reach <see cref="CockpitViewModel.StartProjectSessionCommand"/> rather than silently binding to
/// nothing.
/// </summary>
[Collection("avalonia")]
public class ProjectsWorkspaceCategoryGroupingTests
{
    [Fact]
    public async Task Cards_AreGroupedByCategory_AndStartStillReachesTheRealCommand() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var onboarding = Project.Create("Onboarding flow") with { Category = "Werk", DefaultProfileLabel = "work" };
            var cockpitProject = Project.Create("Cockpit") with { Category = "Privé" };
            var store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings
            {
                Projects = [onboarding, cockpitProject],
                CategoryOrder = ["Werk", "Privé"],
            });

            var dialogs = Substitute.For<ISessionDialogService>();
            var projects = new ProjectsViewModel(store, dialogs);
            await projects.LoadAsync();

            var cockpit = _NewCockpit(dialogs, projects);
            await cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();

            var headings = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Text is "Werk" or "Privé")
                .Select(text => text.Text)
                .ToList();
            Assert.Equal(["Werk", "Privé"], headings);

            var startButton = view.GetVisualDescendants().OfType<Button>()
                .First(button => button.Content is StackPanel panel
                    && panel.Children.OfType<TextBlock>().Any(text => text.Text == "Start"));

            // Proves the nested-ItemsControl $parent binding actually resolved to the real CockpitViewModel command
            // instance rather than silently landing on null (Avalonia logs, but does not throw, a bad binding path).
            Assert.Same(cockpit.StartProjectSessionCommand, startButton.Command);
            Assert.Equal(onboarding.Id, ((Project?)startButton.CommandParameter)?.Id);

            window.Close();
        });

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
