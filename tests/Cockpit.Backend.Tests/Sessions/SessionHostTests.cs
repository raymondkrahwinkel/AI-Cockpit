using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1376: the backend half of an SDK session — its turn gate, its clocks and its numbered event stream — driven
/// with a fake runtime and a hand-cranked clock, without a view model or a dispatcher anywhere near it.
/// </summary>
public class SessionHostTests
{
    private static readonly SessionProfile Profile = new("work", new ClaudeConfig("/fake/.claude"));

    // Layer 2: enforcement overrides approval, and a withdrawn reservation must never dispatch later.
    [Fact]
    public async Task AReadOnlyTurn_RefusesWritingTools_AndWithdrawsBeforeStart()
    {
        var (host, runtime) = _Started();
        runtime.SupportsReadOnlyTurns.Returns(true);
        var provider = Substitute.For<IPluginSessionDriver>();
        provider.SupportsReadOnlyTurns.Returns(true);
        var adapter = new PluginSessionDriverAdapter(provider, new PluginSessionCapabilities(true, true));
        Func<string, bool>? providerFilter = null;
        provider.SetReadOnlyTurnAsync(Arg.Any<Func<string, bool>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            providerFilter = call.Arg<Func<string, bool>?>();
            return Task.CompletedTask;
        });
        var arming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new SessionToolApprovalGate((_, _, _) => { }, (_, _, _) => { }, (_, _, _) => { })
        {
            ToolClasses = new Dictionary<string, ToolPermissionClass>
            {
                ["mcp__depot__write"] = ToolPermissionClass.Write,
                ["Edit"] = ToolPermissionClass.Write,
            },
        };
        gate.SetAutoApprove(true);
        runtime.SetReadOnlyTurnAsync(true, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await adapter.SetReadOnlyTurnAsync(true);
            gate.SetReadOnlyTurn(true);
            await arming.Task;
        });
        using var cancellation = new CancellationTokenSource();

        ReadOnlyTurnReceipt withdrawn = host.TryStartReadOnlyTurn("withdraw this", cancellation.Token);
        Assert.Equal(ReadOnlyTurnAdmission.Accepted, withdrawn.Admission);
        Assert.Equal(ReadOnlyTurnAdmission.Busy, host.TryStartReadOnlyTurn("cannot race").Admission);
        Assert.Single(host.Queue);
        Func<string, bool> filter = Assert.IsType<Func<string, bool>>(providerFilter);
        Assert.False(filter("mcp__depot__write"));
        Assert.False(filter("Edit"));
        Assert.False(filter("Task"));
        Assert.False(filter("Bash"));
        Assert.True(filter("Read"));
        Assert.False((await gate.RequestApprovalAsync("write", "mcp__depot__write", "{}", CancellationToken.None)).Approved);
        Assert.False((await gate.RequestApprovalAsync("edit", "Edit", "{}", CancellationToken.None)).Approved);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn.Started);
        Assert.Empty(host.Queue);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BusyChanged += () => released.TrySetResult();
        arming.SetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.DidNotReceive().SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());

        ReadOnlyTurnReceipt started = host.TryStartReadOnlyTurn("run this");
        await started.Started.WaitAsync(TimeSpan.FromSeconds(5));
        host.Pump(new AssistantTextCompleted { SessionId = "S1", Text = "the answer" });
        host.Pump(_TurnCompleted() with { Result = null });
        Assert.Equal("the answer", await started.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static (SessionHost Host, ISessionRuntime Runtime) _Started(ManualClock? clock = null, ILogger? logger = null)
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var host = new SessionHost(
            () => "pane-a", manager, clock ?? new ManualClock(), loginChecker: loginChecker, logger: logger);
        host.Attach(Profile);
        return (host, runtime);
    }

    private static TurnCompleted _TurnCompleted() =>
        new() { SessionId = "S1", Subtype = "success", Result = "done", IsError = false };

    // A clock whose timers fire only when the test advances it, honouring Change and Dispose as the real ones do.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public ManualTimer LastCreated => _timers[^1];

        public void Advance(TimeSpan by)
        {
            _now += by;
            while (_timers.Where(timer => timer.Due <= _now).MinBy(timer => timer.Due) is { } due)
            {
                due.Fire();
            }
        }

        public sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                _period = period;
                return true;
            }

            public void Fire()
            {
                Due = _period == Timeout.InfiniteTimeSpan ? null : Due + _period;
                callback(state);
            }

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

}
