using System.Reflection;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
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

    /// <summary>
    /// The host never ends a turn on its own: the runtime's TurnCompleted alone leaves the second prompt queued, and
    /// only the consumer's <c>CompleteTurn</c> — its duty when it applies that event — sends it.
    /// </summary>
    [Fact]
    public async Task TheSecondOfTwoPrompts_LeavesOnlyWhenTheConsumerCompletesTheTurn()
    {
        var (host, runtime) = _Started();
        await host.SubmitAsync(new QueuedPrompt("first", []));
        await host.SubmitAsync(new QueuedPrompt("second", []));

        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(_TurnCompleted());

        await _AssertSent(runtime, "second", times: 0);
        Assert.Single(host.Queue);

        host.CompleteTurn();
        await host.DisposeAsync();

        await _AssertSent(runtime, "first", times: 1);
        await _AssertSent(runtime, "second", times: 1);
        Assert.Empty(host.Queue);
    }

    /// <summary>
    /// AC-1321: a turn that ends during a hold starts nothing; lifting the hold sends what waited behind it.
    /// </summary>
    [Fact]
    public async Task AHeldPrompt_WaitsOutTheTurnsEnd_AndLeavesWhenTheHoldLifts()
    {
        var (host, runtime) = _Started();
        await host.SubmitAsync(new QueuedPrompt("first", []));
        await host.SubmitAsync(new QueuedPrompt("waiting", []));
        host.TurnsHeldBecause = "a controller holds the line";

        host.CompleteTurn();

        await _AssertSent(runtime, "waiting", times: 0);
        Assert.Single(host.Queue);

        host.TurnsHeldBecause = null;
        await host.DisposeAsync();

        await _AssertSent(runtime, "waiting", times: 1);
    }

    public static TheoryData<Func<SessionHost<QueuedPrompt>, Func<int>>, TimeSpan> Polls => new()
    {
        {
            host =>
            {
                var checks = 0;
                host.LoginChecked += _ => checks++;
                host.StartLoginPoll(Profile);
                checks = 0;
                return () => checks;
            },
            TimeSpan.FromMinutes(1)
        },
        {
            host =>
            {
                var catchUps = 0;
                host.UsageCatchUpDue += () => catchUps++;
                host.StartUsageCatchUp();
                return () => catchUps;
            },
            TimeSpan.FromSeconds(30)
        },
    };

    /// <summary>
    /// The login poll and the usage catch-up each tick exactly once per interval, and not at all before it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Polls))]
    public void APoll_TicksOncePerInterval_AndNeverBeforeIt(Func<SessionHost<QueuedPrompt>, Func<int>> start, TimeSpan interval)
    {
        var clock = new ManualClock();
        var (host, _) = _Started(clock);
        var ticks = start(host);

        clock.Advance(interval - TimeSpan.FromSeconds(1));
        Assert.Equal(0, ticks());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, ticks());
    }

    /// <summary>
    /// Two sessions raising at once: each stream rises strictly and no number is handed out twice. Only the order is
    /// asserted, since the counter is shared by every host in the process, other tests' included.
    /// </summary>
    [Fact]
    public async Task TheSequence_RisesStrictlyPerSession_AndIsNeverHandedOutTwice()
    {
        var (first, firstRuntime) = _Started();
        var (second, secondRuntime) = _Started();
        var firstSeqs = new List<long>();
        var secondSeqs = new List<long>();
        first.EventAppended += hostEvent => firstSeqs.Add(hostEvent.Seq);
        second.EventAppended += hostEvent => secondSeqs.Add(hostEvent.Seq);

        await Task.WhenAll(
            Task.Run(() => _RaiseMany(firstRuntime, 500)),
            Task.Run(() => _RaiseMany(secondRuntime, 500)));

        Assert.All(firstSeqs.Zip(firstSeqs.Skip(1)), pair => Assert.True(pair.First < pair.Second));
        Assert.All(secondSeqs.Zip(secondSeqs.Skip(1)), pair => Assert.True(pair.First < pair.Second));
        Assert.Equal(1000, firstSeqs.Concat(secondSeqs).Distinct().Count());
    }

    /// <summary>
    /// No member of the host names an Avalonia type, and running a turn through it loads no Avalonia assembly.
    /// Reflecting over the members loads every type they name, so a dispatcher anywhere in them would show below.
    /// </summary>
    [Fact]
    public async Task TheHost_NamesAndLoadsNoAvaloniaType()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = typeof(SessionHost<QueuedPrompt>);
        var (host, runtime) = _Started();
        await host.SubmitAsync(new QueuedPrompt("first", []));
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(_TurnCompleted());
        host.CompleteTurn();

        var named = type.GetFields(all).Select(field => field.FieldType)
            .Concat(type.GetProperties(all).Select(property => property.PropertyType))
            .Concat(type.GetEvents(all).Select(evt => evt.EventHandlerType))
            .Concat(type.GetMethods(all).SelectMany(method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType)))
            .Concat(type.GetConstructors(all).SelectMany(ctor => ctor.GetParameters().Select(p => p.ParameterType)))
            .ToList();

        Assert.NotEmpty(named);
        Assert.DoesNotContain(named, namedType => _IsAvalonia(namedType?.Assembly));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), _IsAvalonia);
    }

    /// <summary>
    /// Moved from <c>SessionViewModelSignOfLifeDisposeTests</c> (AC-786): a clock left armed would keep the pane alive
    /// after it closed. A tick whose timer fires only after a restart replaced it still carries its own, stale arm.
    /// </summary>
    [Fact]
    public async Task TheSignOfLife_KnowsALateTickAsStale_AndStopsForGoodOnDispose()
    {
        var clock = new ManualClock();
        var (host, _) = _Started(clock);
        var arms = new List<int>();
        host.SignOfLifeDue += arms.Add;

        host.RestartSignOfLife(TimeSpan.FromSeconds(10));
        var replaced = clock.LastCreated;
        host.RestartSignOfLife(TimeSpan.FromSeconds(10));
        replaced.Fire();
        Assert.False(host.IsCurrentSignOfLife(Assert.Single(arms)));

        await host.DisposeAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(arms);
    }

    /// <summary>
    /// A tick runs on the pool, where a throw would take the process down: it is logged as an error and the timer
    /// keeps its cadence, as the UI thread's net did for the DispatcherTimers these replace.
    /// </summary>
    [Fact]
    public void AThrowingTick_IsLoggedAsAnError_AndTheTimerKeepsTicking()
    {
        var clock = new ManualClock();
        var logger = new RecordingLogger();
        var (host, _) = _Started(clock, logger);
        var ticks = 0;
        host.UsageCatchUpDue += () =>
        {
            ticks++;
            throw new InvalidOperationException("the handler broke");
        };
        host.StartUsageCatchUp();

        clock.Advance(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(2, ticks);
        Assert.Equal([LogLevel.Error, LogLevel.Error], logger.Levels);
    }

    private static (SessionHost<QueuedPrompt> Host, ISessionRuntime Runtime) _Started(ManualClock? clock = null, ILogger? logger = null)
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var host = new SessionHost<QueuedPrompt>(
            () => "pane-a", manager, clock ?? new ManualClock(), loginChecker: loginChecker, logger: logger);
        host.Attach(Profile);
        return (host, runtime);
    }

    private static Task _AssertSent(ISessionRuntime runtime, string text, int times) =>
        runtime.Received(times).SendUserMessageAsync(text, Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());

    private static TurnCompleted _TurnCompleted() =>
        new() { SessionId = "S1", Subtype = "success", Result = "done", IsError = false };

    private static void _RaiseMany(ISessionRuntime runtime, int count) =>
        Enumerable.Range(0, count).ToList().ForEach(index =>
            runtime.EventAppended += Raise.Event<Action<SessionEvent>>(
                new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = index.ToString() }));

    private static bool _IsAvalonia(Assembly? assembly) =>
        assembly?.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true;

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

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
