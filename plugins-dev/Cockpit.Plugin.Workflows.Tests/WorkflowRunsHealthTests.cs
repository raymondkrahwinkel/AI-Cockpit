using NSubstitute;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Workflows.Tests;

internal sealed class WorkflowRunsHealthTests
{
    internal static async Task VerifyAsync()
    {
        var tests = new WorkflowRunsHealthTests();
        tests.Initialize_RegistersTheRunsSectionAlongsideTheSchedulerHeartbeat();
        tests.Read_ReportsTheLatestOutcomeAndNextRunForEveryActiveScheduledFlow();
        await tests.RunAsync_StartsAScheduleOnlyFlowOnce_AndReportsItsPermissionState();
        tests.Read_UsesOnlyStructuredOutcomesAndNeverLeaksRunText();
    }

    private void Initialize_RegistersTheRunsSectionAlongsideTheSchedulerHeartbeat()
    {
        var storage = new InMemoryPluginStorage();
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(storage);
        host.Cache.Returns(storage);
        host.WorkflowSteps.Returns([]);
        using var plugin = new WorkflowsPlugin();

        plugin.Initialize(host);

        host.Received().AddHealthSection(Arg.Is<IPluginHealthSection>(section => section.Name == "workflows-runs"));
        host.Received().AddHealthSection(Arg.Is<IPluginHealthSection>(section => section.Name == "workflows-scheduler"));
    }

    private void Read_ReportsTheLatestOutcomeAndNextRunForEveryActiveScheduledFlow()
    {
        var storage = new InMemoryPluginStorage();
        var store = new WorkflowStore(storage);
        var runs = new RunStore(storage);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var flow = _ScheduledFlow("daily-checkin", "Daily check-in", "18:30", "Europe/Amsterdam");
        store.Save([flow]);
        runs.Add(new WorkflowRun
        {
            Id = "run-1",
            WorkflowId = flow.Id,
            WorkflowName = flow.Name,
            StartedAt = new DateTimeOffset(2026, 10, 2, 16, 30, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 10, 2, 16, 34, 0, TimeSpan.Zero),
            Status = RunStatus.Succeeded,
        });

        using var watcher = new FlowWatcher(store, runs, new ScheduleMarks(storage), Substitute.For<ICockpitHost>(), () => TimeSpan.Zero, clock);
        var report = new WorkflowRunsHealth(store, runs, watcher, clock).Read();

        Assert.True(report.Healthy);
        Assert.Collection(report.Rows,
            summary =>
            {
                Assert.Equal("1 scheduled · next: Daily check-in", summary.Label);
                Assert.Equal(new DateTimeOffset(2026, 10, 3, 16, 30, 0, TimeSpan.Zero), summary.At);
            },
            outcome =>
            {
                Assert.Equal("Daily check-in · Done · 4 m", outcome.Label);
                Assert.Equal(PluginHealthStatus.Ok, outcome.Status);
                Assert.Equal(new DateTimeOffset(2026, 10, 2, 16, 34, 0, TimeSpan.Zero), outcome.At);
                Assert.NotNull(outcome.ActionId);
                Assert.Null(outcome.ProjectId);
            },
            next =>
            {
                Assert.Equal("Daily check-in · Next run", next.Label);
                Assert.Equal(new DateTimeOffset(2026, 10, 3, 16, 30, 0, TimeSpan.Zero), next.At);
                Assert.Null(next.ActionId);
            });
    }

    private async Task RunAsync_StartsAScheduleOnlyFlowOnce_AndReportsItsPermissionState()
    {
        var storage = new InMemoryPluginStorage();
        var store = new WorkflowStore(storage);
        var recorded = new TaskCompletionSource<WorkflowRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new RunStore(storage, run => recorded.TrySetResult(run));
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var flow = _ScheduledFlow("approval", "Approval", "18:30", "Europe/Amsterdam");
        var approval = new WorkflowNode
        {
            Id = "approval-step",
            TypeId = "cockpit.approve",
            Name = "Ask me first",
            Parameters = { ["Question"] = "Continue?" },
        };
        flow.Nodes.Add(approval);
        flow.Connect("schedule", 0, approval.Id);
        store.Save([flow]);

        var decision = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(decision.Task);
        using var watcher = new FlowWatcher(store, runs, new ScheduleMarks(storage), host, () => TimeSpan.Zero, clock);
        var health = new WorkflowRunsHealth(store, runs, watcher, clock);
        var actionId = Assert.IsType<string>(health.Read().Rows.Single(row => row.ActionId is not null).ActionId);

        var started = await health.RunAsync(actionId, CancellationToken.None);
        var duplicate = await health.RunAsync(actionId, CancellationToken.None);

        Assert.True(started.Succeeded);
        Assert.False(duplicate.Succeeded);
        Assert.Contains(health.Read().Rows, row => row.Label == "Approval · Waiting for your permission");

        decision.SetResult(ConsentDecision.Denied);
        var run = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(run.IsManual);
        Assert.Equal(WorkflowRunReason.ConsentDenied, run.Reason);
        Assert.Contains(health.Read().Rows, row => row.Label == "Approval · Not run · Consent denied");
    }

    private void Read_UsesOnlyStructuredOutcomesAndNeverLeaksRunText()
    {
        var storage = new InMemoryPluginStorage();
        var store = new WorkflowStore(storage);
        var runs = new RunStore(storage);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var missed = _ScheduledFlow("missed", "Missed flow", "18:30", "Europe/Amsterdam");
        var caughtUp = _ScheduledFlow("caught-up", "Caught-up flow", "18:30", "Europe/Amsterdam");
        var discord = _ScheduledFlow("discord", "Discord flow", "18:30", "Europe/Amsterdam");
        var approved = _ScheduledFlow("approved", "Approved flow", "18:30", "Europe/Amsterdam");
        var failed = _ScheduledFlow("failed", "Failed flow", "18:30", "Europe/Amsterdam");
        store.Save([missed, caughtUp, discord, approved, failed]);

        runs.Add(_Run(missed, RunStatus.Skipped, reason: WorkflowRunReason.Missed));
        runs.Add(_Run(caughtUp, RunStatus.Succeeded, caughtUp: true));
        var discordRun = _Run(discord, RunStatus.Succeeded);
        discordRun.Steps.Add(new StepRun
        {
            NodeId = "send",
            NodeName = "Send",
            TypeId = "discord.send-dm",
            Status = RunStatus.Succeeded,
            StartedAt = clock.GetUtcNow(),
            FinishedAt = clock.GetUtcNow(),
        });
        runs.Add(discordRun);
        var approvedRun = _Run(approved, RunStatus.Succeeded);
        approvedRun.ApprovalOrigin = WorkflowApprovalOrigin.Discord;
        runs.Add(approvedRun);
        var failedRun = _Run(failed, RunStatus.Failed);
        failedRun.Error = "C:\\private\\token.txt on secret.example for channel 123 and user 456";
        failedRun.Note = "token=do-not-leak";
        runs.Add(failedRun);

        using var watcher = new FlowWatcher(store, runs, new ScheduleMarks(storage), Substitute.For<ICockpitHost>(), () => TimeSpan.Zero, clock);
        var rows = new WorkflowRunsHealth(store, runs, watcher, clock).Read().Rows;
        var labels = rows.Select(row => row.Label).ToList();

        Assert.Contains("Missed flow · Not run · Missed", labels);
        Assert.Contains("Caught-up flow · Missed, caught up", labels);
        Assert.Contains("Discord flow · Sent to Discord", labels);
        Assert.Contains("Approved flow · Approved in Discord", labels);
        Assert.Contains("Failed flow · Failed", labels);
        Assert.Equal(PluginHealthStatus.Failed, rows.Single(row => row.Label == "Missed flow · Not run · Missed").Status);
        Assert.Equal(PluginHealthStatus.Ok, rows.Single(row => row.Label == "Caught-up flow · Missed, caught up").Status);
        Assert.DoesNotContain(labels, label => label.Contains("private", StringComparison.OrdinalIgnoreCase)
            || label.Contains("secret.example", StringComparison.OrdinalIgnoreCase)
            || label.Contains("do-not-leak", StringComparison.OrdinalIgnoreCase));
    }

    private static Workflow _ScheduledFlow(string id, string name, string when, string zone) => new()
    {
        Id = id,
        Name = name,
        IsActive = true,
        Nodes =
        {
            new WorkflowNode
            {
                Id = "schedule",
                TypeId = "cockpit.schedule",
                Name = "Schedule",
                Parameters =
                {
                    ["When"] = when,
                    ["Time zone"] = zone,
                },
            },
        },
    };

    private static WorkflowRun _Run(
        Workflow workflow,
        RunStatus status,
        WorkflowRunReason reason = WorkflowRunReason.None,
        bool caughtUp = false) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        WorkflowId = workflow.Id,
        WorkflowName = workflow.Name,
        StartedAt = new DateTimeOffset(2026, 10, 3, 11, 58, 0, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
        Status = status,
        Phase = WorkflowRunPhase.Completed,
        Reason = reason,
        WasCaughtUp = caughtUp,
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FixedTimer();

        private sealed class FixedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

}
