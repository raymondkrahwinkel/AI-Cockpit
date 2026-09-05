using Cockpit.App.Plugins;
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
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Worktrees;
using Cockpit.Plugins.Abstractions.Sessions;
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
        Assert.Empty(vm.Sessions);
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

        Assert.Single(vm.Sessions);
    }

    [Fact]
    public async Task StartProjectJob_OpensTheDialogCarryingThatJobsOwnPrompt()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(Confirmed());
        var vm = NewVm(dialogs);
        var job = new ProjectJob("Process this month's invoices", "changes nothing · reports only");
        var project = Project.Create("Invoices") with { Jobs = [job] };

        await vm.StartProjectJobCommand.ExecuteAsync(new ProjectJobChoice(project, job));

        // The prompt itself, not merely that a dialog opened on the project — the plain Start does that much too.
        await dialogs.Received(1).ShowNewSessionDialogAsync(
            Arg.Is<NewSessionPrefill?>(prefill => prefill!.InitialPrompt == job.Prompt),
            Arg.Any<bool>(),
            Arg.Is<Project?>(passed => passed == project));
    }

    [Fact]
    public async Task NewSessionForProject_OnAProjectOfferingNoJobs_CarriesNoPrefillAtAll()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(Confirmed());
        var vm = NewVm(dialogs);
        var project = Project.Create("Cockpit");

        await vm.NewSessionForProjectCommand.ExecuteAsync(project);

        // AC-491 reaches the start path of every session, this one included: a project that offers no work must
        // reach the dialog exactly as it did before jobs existed — nothing pre-filled, not even an empty prompt.
        await dialogs.Received(1).ShowNewSessionDialogAsync(
            null, Arg.Any<bool>(), Arg.Is<Project?>(passed => passed == project));
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
        var quickStart = new ProjectQuickStart(profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());
        var vm = NewVm(Substitute.For<ISessionDialogService>(), quickStart: quickStart);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);
        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        // Two rows both reading "Cockpit" is exactly what the dialog's own numbering avoids when it generates a name.
        Assert.Equal(new[] { "Cockpit", "Cockpit 2" }, vm.Sessions.Select(session => session.Title));
    }

    [Fact]
    public async Task StartProjectSession_NamesItAfterTheProject_WithoutClaimingTheNameAsChosen()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());
        var vm = NewVm(Substitute.For<ISessionDialogService>(), quickStart: quickStart);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        // "Cockpit" is composed here from the project, not typed by anyone, so linking a ticket to this session may
        // still label it — the same as a session that was never named at all (#AC-310).
        var session = vm.Sessions.Single();
        Assert.Equal("Cockpit", session.Title);
        Assert.True(vm.SuggestSessionName(session.PaneId, "AC-310"));
        Assert.Equal("AC-310", session.Title);
    }

    [Fact]
    public async Task StartingASession_RecordsThatTheProjectWasWorkedOn()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var dialogs = Substitute.For<ISessionDialogService>();
        var projects = new ProjectsViewModel(store, dialogs);
        await projects.LoadAsync();

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"))]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var vm = NewVm(dialogs, projects, new ProjectQuickStart(profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry()));

        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        // Recorded wherever the session came from, so the overview leads with what is actually used rather than
        // the order the projects happen to be stored in.
        await store.Received().SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LastOpenedAt != null),
            Arg.Any<CancellationToken>());
    }

    // The route that had the bug (#AC-312): its name is put together from the profile and the clock, so it is nobody's
    // and a ticket linked to the session later may still label it. This is the only start route no test watched, which
    // is how it came to be the one that forgot (#AC-324).
    [Fact]
    public async Task StartSessionForPlugin_WithoutAName_LeavesTheSessionOpenToBeingLabelled()
    {
        var vm = NewVm(Substitute.For<ISessionDialogService>());
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"));

        await vm.StartSessionForPluginAsync(profile, prompt: null, workingDirectory: null);

        var session = vm.Sessions.Single();
        Assert.True(vm.SuggestSessionName(session.PaneId, "AC-312"));
        Assert.Equal("AC-312", session.Title);
    }

    [Fact]
    public async Task StartSessionForPlugin_WithAName_KeepsIt()
    {
        var vm = NewVm(Substitute.For<ISessionDialogService>());
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"));

        await vm.StartSessionForPluginAsync(profile, prompt: null, workingDirectory: null, sessionName: "release work");

        // A name the caller passed is a decision, so a ticket offers its own rather than taking this one.
        var session = vm.Sessions.Single();
        Assert.Equal("release work", session.Title);
        Assert.False(vm.SuggestSessionName(session.PaneId, "AC-312"));
        Assert.Equal("release work", session.Title);
    }

    [Fact]
    public async Task OpenProjectsWorkspace_BringsUpTheOverview()
    {
        var vm = NewVm(Substitute.For<ISessionDialogService>());

        await vm.OpenProjectsWorkspaceCommand.ExecuteAsync(null);

        Assert.True(vm.Workspaces.IsProjectsActive);
        Assert.Equal("Projects", vm.Workspaces.Active!.Name);
    }

    [Fact]
    public async Task ManageProjects_OpensTheProjectsManager_NotOptions()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings());
        var projects = new ProjectsViewModel(store, dialogs);
        var vm = NewVm(dialogs, projects);

        await vm.ManageProjectsCommand.ExecuteAsync(null);

        // Its own window (Raymond, 2026-07-24): a project is the work the cockpit is pointed at, not a setting of
        // it, and where projects come from is about to widen beyond this machine.
        await dialogs.Received(1).ShowProjectsDialogAsync(projects);
        await dialogs.DidNotReceive().ShowOptionsDialogAsync(Arg.Any<CockpitViewModel>());
    }

    private static NewSessionResult Confirmed() => new(
        SessionKind.Sdk,
        new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort,
        SessionName: null);

    // The quick-start route skips the New-session dialog's isolate-checkbox graying, so it is the door where the
    // silent isolation degradation this ticket fixes actually showed up.
    [Fact]
    public async Task StartProjectSession_IsolateOnWithNoWorkingDirectory_AsksInsteadOfStartingUnisolatedSilently()
    {
        var (vm, dialogs, _) = _BuildIsolationTestVm();
        var project = Project.Create("Admin") with { DefaultProfileLabel = "work", IsolateInWorktreeByDefault = true };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        await dialogs.Received(1).ShowConfirmationDialogAsync(
            "Could not isolate this session",
            Arg.Is<string>(message => message.Contains("no working directory is set", StringComparison.Ordinal)),
            "Run in folder");
        // The stub below accepts "Run in folder" — the session still starts, just not isolated, exactly the
        // outcome a decline should still leave available.
        Assert.Single(vm.Sessions);
    }

    [Fact]
    public async Task StartProjectSession_IsolateOnWithANonRepositoryFolder_AsksInsteadOfStartingUnisolatedSilently()
    {
        var (vm, dialogs, worktrees) = _BuildIsolationTestVm();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        // DetectRepositoryAsync is left unconfigured, which answers null — "not a git repository here", the
        // exact condition CockpitViewModel.cs:5171 used to return the folder from silently instead of throwing.
        var project = Project.Create("Cockpit") with
        {
            DefaultProfileLabel = "work",
            SourceDirectories = [new("/not/a/repo")],
            IsolateInWorktreeByDefault = true,
        };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        await dialogs.Received(1).ShowConfirmationDialogAsync(
            "Could not isolate this session",
            Arg.Is<string>(message => message.Contains("is not a git repository", StringComparison.Ordinal)),
            "Run in folder");
        Assert.Single(vm.Sessions);
    }

    private static (CockpitViewModel Vm, ISessionDialogService Dialogs, IWorktreeManager Worktrees) _BuildIsolationTestVm()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());
        var worktrees = Substitute.For<IWorktreeManager>();
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        return (NewVm(dialogs, quickStart: quickStart, worktreeManager: worktrees), dialogs, worktrees);
    }

    private static CockpitViewModel NewVm(
        ISessionDialogService dialogs,
        ProjectsViewModel? projects = null,
        ProjectQuickStart? quickStart = null,
        IWorktreeManager? worktreeManager = null)
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
            worktreeManager: worktreeManager,
            projects: projects,
            projectQuickStart: quickStart);
    }
}
