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
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-490 criterion 2: a session started from a job is recorded as a run of that job, against the project and the
/// job id — and only once the save carrying that id has succeeded, so no run can point at an id the next load would
/// not know. A plain project session is not a run: without a job there is no unit of work to attribute it to.
/// </summary>
public class Ac490ProjectJobRunTests
{
    [Fact]
    public async Task StartingAJob_RecordsARunAgainstProjectAndJob_OnlyAfterTheProjectSaved_AndAPlainStartRecordsNothing()
    {
        var job = new ProjectJob("Process this month's invoices", "changes nothing · reports only");
        var other = new ProjectJob("Chase the unpaid ones", "sends reminders");
        var project = Project.Create("Invoices") with { DefaultProfileLabel = "work", Jobs = [job, other] };

        // A store that saves: the run is written, and it names exactly the job that was started.
        var store = _Store(project);
        var (vm, history) = await _CockpitAsync(store);
        await vm.StartProjectJobNowCommand.ExecuteAsync(new ProjectJobChoice(project, job));

        var started = Assert.Single(history.Events);
        Assert.Equal(ProjectJobRunEventKind.Started, started.Kind);
        Assert.Equal(project.Id, started.ProjectId);
        Assert.Equal(job.Id, started.JobId);
        Assert.Equal(Assert.Single(vm.Sessions).PaneId, started.PaneId);
        Assert.Single(await history.ReadRecentRunsAsync(), run => run.JobId == job.Id);
        Assert.DoesNotContain(await history.ReadRecentRunsAsync(), run => run.JobId == other.Id);

        // The same start without a job: a session, not a run — the usage trail already says a session existed.
        await vm.StartProjectSessionCommand.ExecuteAsync(project);
        Assert.Single(history.Events);

        // A store whose save fails: no line at all, rather than a line pointing at an id that never reached disk.
        var failing = _Store(project);
        failing.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disk full")));
        var (failingVm, failingHistory) = await _CockpitAsync(failing);
        await failingVm.StartProjectJobNowCommand.ExecuteAsync(new ProjectJobChoice(project, job));

        Assert.Single(failingVm.Sessions);
        Assert.Empty(failingHistory.Events);
    }

    private static IProjectStore _Store(Project project)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty.WithProject(project));
        store.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return store;
    }

    private static async Task<(CockpitViewModel Vm, RecordingJobHistory History)> _CockpitAsync(IProjectStore store)
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"))
        {
            DefaultKind = ProfileSessionKind.Sdk,
        };
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(
            profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());

        var history = new RecordingJobHistory();
        var projects = new ProjectsViewModel(store, dialogs: null, jobHistory: history);
        await projects.LoadAsync();

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

        var vm = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcripts,
            behavior,
            layout,
            voice,
            terminals,
            projects: projects,
            projectQuickStart: quickStart,
            projectJobHistory: history);

        return (vm, history);
    }

    // The trail as a list, so a test can read what was written without a file.
    private sealed class RecordingJobHistory : IProjectJobHistory
    {
        public List<ProjectJobRunEvent> Events { get; } = [];

        public Task RecordAsync(ProjectJobRunEvent entry, CancellationToken cancellationToken = default)
        {
            Events.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectJobRun>> ReadRecentRunsAsync(int eventLimit = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectJobRun.Fold(Events));
    }
}
