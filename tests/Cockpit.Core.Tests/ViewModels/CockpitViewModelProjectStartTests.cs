using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Starting a session from a project (AC-164): the sidebar's ▶ and its context menu. The quick start itself is
/// <see cref="ProjectQuickStart"/>'s; what is exercised here is that the cockpit launches what it composes and
/// asks the operator when it composes nothing.
/// </summary>
public class CockpitViewModelProjectStartTests
{
    [Fact]
    public async Task StartProjectSession_WhenNothingCanBeComposed_OpensTheDialogOnThatProject()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns((NewSessionResult?)null);
        var vm = NewVm(dialogs);
        var project = Project.Create("Cockpit");

        // No ProjectQuickStart in this graph, so composing yields nothing — the same outcome as a project whose
        // profile is gone. Falling through to the dialog asks rather than failing quietly.
        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        await dialogs.Received(1).ShowNewSessionDialogAsync(
            Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Is<Project?>(passed => passed == project));
        vm.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task NewSessionForProject_StartsWhatTheDialogConfirms()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(Confirmed());
        var vm = NewVm(dialogs);
        var project = Project.Create("Cockpit");

        await vm.NewSessionForProjectCommand.ExecuteAsync(project);

        vm.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task EditProject_OpensTheEditorForThatProject()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings());
        var projects = new ProjectsViewModel(store, dialogs);
        var project = Project.Create("Cockpit");
        var vm = NewVm(dialogs, projects);

        await vm.EditProjectCommand.ExecuteAsync(project);

        await dialogs.Received(1).ShowProjectDialogAsync(project);
    }

    [Fact]
    public async Task StartProjectSession_Twice_NumbersTheSecondSession()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(profiles, catalog, Substitute.For<ITtySessionProviderResolver>());
        var vm = NewVm(Substitute.For<ISessionDialogService>(), quickStart: quickStart);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);
        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        // Two rows both reading "Cockpit" is exactly what the dialog's own numbering avoids when it generates a name.
        vm.Sessions.Select(session => session.Title).Should().Equal("Cockpit", "Cockpit 2");
    }

    [Fact]
    public async Task ManageProjects_OpensOptionsOnTheProjectsTab()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogs);

        await vm.ManageProjectsCommand.ExecuteAsync(null);

        // The launcher's "Manage projects" is that tab and nothing else — landing on the first tab would make the
        // operator go looking for what the button named.
        await dialogs.Received(1).ShowOptionsDialogAsync(vm, "Projects");
    }

    private static NewSessionResult Confirmed() => new(
        SessionKind.Sdk,
        new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort,
        SessionName: null);

    private static CockpitViewModel NewVm(
        ISessionDialogService dialogs,
        ProjectsViewModel? projects = null,
        ProjectQuickStart? quickStart = null)
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
            projects: projects,
            projectQuickStart: quickStart);
    }
}
