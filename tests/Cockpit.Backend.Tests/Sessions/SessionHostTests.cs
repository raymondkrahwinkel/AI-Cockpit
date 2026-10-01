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

    public static TheoryData<Func<SessionHost, Func<int>>, TimeSpan> Polls => new()
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
        var type = typeof(SessionHost);
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
