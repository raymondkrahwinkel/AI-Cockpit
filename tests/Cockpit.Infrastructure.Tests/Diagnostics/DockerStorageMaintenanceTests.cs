using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Assistant;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Diagnostics;
using Cockpit.Infrastructure.HeavyRuns;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions.HeavyRuns;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Diagnostics;

public sealed class DockerStorageMaintenanceTests
{
    [Fact]
    public async Task PressureCleanupPreservesTheRunningContainerAndItsDatabaseAndReportsReclaimedSpace()
    {
        await using var test = new Fixture();
        await test.Maintenance.CleanupAsync();
        Assert.True(test.Docker.ContainerRunning);
        Assert.Equal("database answers", test.Docker.QueryDatabase());
        Assert.Equal(new[] { "system df --format json", "builder prune -f --filter until=24h", "image prune -a -f --filter until=24h", "system df --format json" }, test.Docker.Calls);
        Assert.Contains(test.Toasts.Messages, message => message.Contains("3GB") && message.Contains("11GB"));
        Assert.Contains(test.Inbox.Deliveries, delivery => delivery.Kind == "storage maintenance");
        Assert.DoesNotContain(test.Docker.Calls, command => command.Contains("volume") || command.Contains(" rm"));
        Assert.True(test.Maintenance.CanCompact);
        Assert.Empty(test.Platform.Calls);
        await test.Maintenance.RequestCompactionAsync();
        Assert.DoesNotContain("desktop stop", test.Docker.Calls);
        Assert.Empty(test.Elevated.Commands);
        test.Toasts.Actions.Single(action => action.Label == "Cancel").OnInvoke();
        Assert.Empty(test.Elevated.Commands);
        test.Platform.Windows = false;
        await test.Maintenance.RequestCompactionAsync();
        Assert.Contains(test.Toasts.Messages, message => message.Contains("skipped on Linux"));
        foreach (var (pressure, hours) in new[] { (DiskPressure.Normal, 72), (DiskPressure.Critical, 2) })
        {
            await using var retention = new Fixture(pressure);
            await retention.Maintenance.CleanupAsync();
            Assert.Contains($"builder prune -f --filter until={hours}h", retention.Docker.Calls);
            Assert.Contains($"image prune -a -f --filter until={hours}h", retention.Docker.Calls);
            Assert.Equal("database answers", retention.Docker.QueryDatabase());
            if (pressure == DiskPressure.Critical)
            {
                await using var waiting = retention.Queue.Request(retention.Request("critical-disk", HeavyRunPurpose.Explore));
                Assert.False(waiting.Admitted.IsCompleted);
                Assert.Contains("critical", retention.Queue.StatusOf("critical-disk")!.Value.Reason);
            }
        }
    }

    [Fact]
    public async Task CompactionWaitsForOperatorDrainAndSessionAcknowledgementAndAlwaysRecoversDockerAndQueue()
    {
        foreach (var outcome in new[] { ElevatedCommandOutcome.Completed, ElevatedCommandOutcome.Declined, ElevatedCommandOutcome.Failed, ElevatedCommandOutcome.TimedOut })
        {
            await using var test = new Fixture();
            test.Elevated.Outcome = outcome;
            test.Live.LiveSessionIds.Returns(new HashSet<string> { "session", "background", "idle", "done", "failed", AssistantIdentity.PaneId });
            var busy = Substitute.For<ISessionHandle>();
            busy.SessionStatus.Returns(SessionStatus.Busy);
            test.Sessions.Find("session").Returns(busy);
            var background = Substitute.For<ISessionHandle>();
            background.SessionStatus.Returns(SessionStatus.Idle);
            background.HasOutstandingBackgroundShellsAsync().Returns(true);
            test.Sessions.Find("background").Returns(background);
            foreach (var (pane, status) in new[] { ("idle", SessionStatus.Idle), ("done", SessionStatus.Done), ("failed", SessionStatus.Failed) })
            {
                var idle = Substitute.For<ISessionHandle>();
                idle.SessionStatus.Returns(status);
                idle.HasOutstandingBackgroundShellsAsync().Returns(false);
                test.Sessions.Find(pane).Returns(idle);
            }
            await using var running = test.Queue.Request(test.Request("existing", HeavyRunPurpose.Operator));
            await test.Maintenance.RequestCompactionAsync();
            Assert.Contains(test.Toasts.Messages, message => message.Contains("Ubuntu") && message.Contains("docker-desktop"));
            Assert.Empty(test.Elevated.Commands);
            Assert.DoesNotContain("desktop stop", test.Docker.Calls);
            test.Toasts.Actions.Single(action => action.Label == "Compact").OnInvoke();
            var queued = test.Queue.Request(test.Request("next", HeavyRunPurpose.Operator));
            Assert.False(queued.Admitted.IsCompleted);
            Assert.DoesNotContain(test.Inbox.Deliveries, delivery => delivery.Kind == "storage pause");
            await running.DisposeAsync();
            await test.Inbox.PauseSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain("desktop stop", test.Docker.Calls);
            var pause = Assert.Single(test.Inbox.Deliveries, delivery => delivery.Kind == "storage pause" && delivery.PaneId == "session");
            var id = pause.Body.Split("pauseId=", StringSplitOptions.None)[1].Split('.')[0];
            Assert.False(test.Maintenance.AcknowledgePause("other-session", id));
            Assert.False(test.Maintenance.AcknowledgePause("session", "stale-id"));
            Assert.True(test.Maintenance.AcknowledgePause("session", id));
            Assert.False(test.Maintenance.Compaction.IsCompleted);
            Assert.DoesNotContain("desktop stop", test.Docker.Calls);
            Assert.False(test.Maintenance.AcknowledgePause(AssistantIdentity.PaneId, id));
            Assert.True(test.Maintenance.AcknowledgePause("background", id));
            await test.Maintenance.Compaction.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(5, test.Inbox.Deliveries.Count(delivery => delivery.Kind == "storage pause"));
            Assert.Equal(5, test.Inbox.Deliveries.Count(delivery => delivery.Kind == "storage resume"));
            Assert.Single(test.Elevated.Commands);
            Assert.Contains("attach vdisk readonly", test.Elevated.Commands[0]);
            Assert.Equal(new[] { "stop", "shutdown", "elevate", "start", "ready" }, test.Order);
            Assert.Contains(test.Inbox.Deliveries, delivery => delivery.Kind == "storage resume");
            Assert.True(queued.Admitted.IsCompletedSuccessfully);
            await queued.DisposeAsync();
            await using var reservation = test.Queue.ReserveMaintenance(Environment.CurrentDirectory);
            Assert.Throws<InvalidOperationException>(() => test.Queue.ReserveMaintenance(Environment.CurrentDirectory));
            var stopped = test.Queue.Snapshot().Single(activity => activity.Title.Contains("maintenance", StringComparison.OrdinalIgnoreCase));
            await stopped.StopAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Queue.WaitForIdleAsync(reservation, CancellationToken.None));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _costPath = Path.Combine(Path.GetTempPath(), "cockpit-storage-test-" + Guid.NewGuid().ToString("N"));
        public readonly List<string> Order = [];
        public readonly FakeDocker Docker;
        public readonly FakePlatform Platform;
        public readonly FakeElevated Elevated;
        public readonly FakeToasts Toasts = new();
        public readonly FakeInbox Inbox = new();
        public readonly ILiveSessionRegistry Live = Substitute.For<ILiveSessionRegistry>();
        public readonly ISessionRegistry Sessions = Substitute.For<ISessionRegistry>();
        public readonly HeavyRunQueue Queue;
        public readonly DockerStorageMaintenance Maintenance;

        public Fixture(DiskPressure pressure = DiskPressure.Elevated)
        {
            Docker = new FakeDocker(Order);
            Platform = new FakePlatform(Order);
            Elevated = new FakeElevated(Order);
            var resources = Substitute.For<IMachineResources>();
            resources.Current.Returns(new MachineLoad(0, 10_000_000_000, 20_000_000_000));
            resources.Disks.Returns(new[] { new DiskSpaceSnapshot(Path.GetPathRoot(Environment.CurrentDirectory)!, 1_000_000_000_000, 20_000_000_000, TimeSpan.FromHours(2), pressure) });
            resources.FreeDiskBytes(Arg.Any<string>()).Returns(20_000_000_000);
            var settings = Substitute.For<IHeavyRunSettingsStore>();
            settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new HeavyRunSettings());
            Live.LiveSessionIds.Returns(new HashSet<string>());
            Queue = new HeavyRunQueue(resources, settings, new HeavyRunCostStore(_costPath));
            Maintenance = new DockerStorageMaintenance(resources, Docker, Platform, Elevated, Queue, Inbox, Live, Sessions, NullLogger<DockerStorageMaintenance>.Instance, Toasts);
        }

        public HeavyRunRequest Request(string id, HeavyRunPurpose purpose) => new(id, HeavyRunKind.Build, purpose, "session", id, Environment.CurrentDirectory);

        public async ValueTask DisposeAsync()
        {
            await Maintenance.DisposeAsync();
            Queue.Dispose();
            File.Delete(_costPath);
        }
    }

    private sealed class FakeDocker(List<string> order) : IDockerCli
    {
        public readonly List<string> Calls = [];
        public bool ContainerRunning { get; private set; } = true;
        private bool _databaseExists = true;
        public string QueryDatabase() => ContainerRunning && _databaseExists ? "database answers" : throw new InvalidOperationException("Database lost.");
        public Task<DockerCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = string.Join(' ', arguments);
            Calls.Add(command);
            if (arguments.Contains("--volumes") || arguments.Contains("volume")) { _databaseExists = false; }
            if (arguments.Contains("rm")) { ContainerRunning = false; }
            if (command == "desktop stop") { order.Add("stop"); }
            if (command == "desktop start") { order.Add("start"); }
            if (command == "version") { order.Add("ready"); }
            var output = command.StartsWith("system df", StringComparison.Ordinal)
                ? """[{"Type":"Images","Size":"11GB","Reclaimable":"10GB (90%)"},{"Type":"Containers","Size":"1GB","Reclaimable":"0B (0%)"},{"Type":"Local Volumes","Size":"1GB","Reclaimable":"0B (0%)"},{"Type":"Build Cache","Size":"3GB","Reclaimable":"3GB (100%)"}]"""
                : "Total reclaimed space: " + (arguments[0] == "builder" ? "3GB" : "11GB");
            return Task.FromResult(new DockerCliResult(0, output, string.Empty));
        }
    }

    private sealed class FakePlatform(List<string> order) : IDockerStoragePlatform
    {
        public readonly List<string> Calls = [];
        public bool Windows = true;
        public bool IsWindows => Windows;
        public DockerDataDisk ReadDataDisk() => new(Path.Combine(Environment.CurrentDirectory, "docker_data.vhdx"), 517_000_000_000, 517_000_000_000);
        public Task<IReadOnlyList<string>> RunningDistrosAsync(CancellationToken cancellationToken)
        {
            Calls.Add("list");
            return Task.FromResult<IReadOnlyList<string>>(["Ubuntu", "docker-desktop"]);
        }

        public Task ShutdownAsync(CancellationToken cancellationToken) { order.Add("shutdown"); return Task.CompletedTask; }
    }

    private sealed class FakeElevated(List<string> order) : IElevatedCommandRunner
    {
        public readonly List<string> Commands = [];
        public ElevatedCommandOutcome Outcome = ElevatedCommandOutcome.Completed;
        public bool IsSupported => true;
        public ElevatedCommandPlan Plan(string command) => new("fake", command, "fake-output");
        public Task<ElevatedCommandResult> RunAsync(ElevatedCommandPlan plan, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            order.Add("elevate");
            Commands.Add(plan.Arguments);
            return Task.FromResult(new ElevatedCommandResult(Outcome, Outcome == ElevatedCommandOutcome.Completed ? 0 : null, string.Empty));
        }
    }

    private sealed class FakeToasts : IToastService
    {
        public readonly List<string> Messages = [];
        public IReadOnlyList<ToastAction> Actions = [];
        public void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null) => Messages.Add(message);
        public void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky) { Messages.Add(message); Actions = actions; }
    }

    private sealed class FakeInbox : IAgentMessageInbox
    {
        private readonly IAgentMessageInbox _unused = Substitute.For<IAgentMessageInbox>();
        private readonly List<(string PaneId, string Kind, string Body)> _deliveries = [];
        public readonly TaskCompletionSource PauseSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Snapshot under lock; PauseSent fires only once all 5 pause messages are delivered.
        public (string PaneId, string Kind, string Body)[] Deliveries { get { lock (_deliveries) { return [.. _deliveries]; } } }
        public AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body, string? senderProfile = null, string? senderProjectId = null, bool profileWide = false, string? subjectPaneId = null)
        {
            lock (_deliveries)
            {
                _deliveries.Add((toPaneId, kind, body));
                if (_deliveries.Count(d => d.Kind == "storage pause") == 5) { PauseSent.TrySetResult(); }
            }
            return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Delivered, null);
        }
        public AgentInboxBatch? ReadFor(string paneId, string? afterMessageId, Func<Cockpit.Core.Mcp.ActiveController?, bool> isReader, Func<AgentMessage, bool> mayRead, int limit) => throw new NotSupportedException();
        public AgentInboxBatch Drain(string paneId, int limit) => _unused.Drain(paneId, limit);
        public AgentMessage? PeekOldest(string paneId) => null;
        public AgentInboxBatch TakeForDelivery(string paneId, int limit) => _unused.TakeForDelivery(paneId, limit);
        public void ConfirmDelivered(string paneId, IReadOnlyList<string> messageIds) { }
        public void ReturnUndelivered(string paneId, IReadOnlyList<string> messageIds) { }
        public bool Retract(string toPaneId, string messageId) => false;
        public void Forget(string paneId) { }
    }
}
