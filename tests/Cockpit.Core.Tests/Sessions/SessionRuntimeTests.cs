using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The headless session runtime (#68): it owns the driver, pumps its events on a plain task, and keeps what a
/// consumer needs — the event log, the last reply — without a UI thread anywhere in sight. These tests drive it
/// with no Avalonia at all, which is exactly the property that makes a delegated task (#67) possible.
/// </summary>
public class SessionRuntimeTests
{
    [Fact]
    public async Task StartAsync_PumpsDriverEventsToSubscribers_WithoutAUiThread()
    {
        var driver = _DriverEmitting(
            new AssistantTextCompleted { SessionId = "s1", Text = "hello" },
            new TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);
        var seen = new List<SessionEvent>();
        runtime.EventAppended += seen.Add;

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime, expectedEvents: 2);

        Assert.Equal(2, seen.Count);
        Assert.True(runtime.IsRunning);
    }

    [Fact]
    public async Task LastAssistantText_FoldsAWholeTurn_NotJustItsLastBlock()
    {
        // A turn can produce prose, then a tool call, then more prose. A delegated task asks for "the result",
        // so the runtime hands back the whole reply rather than the final fragment.
        var driver = _DriverEmitting(
            new AssistantTextCompleted { SessionId = "s1", Text = "first" },
            new AssistantTextCompleted { SessionId = "s1", Text = "second" },
            new TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime, expectedEvents: 3);

        Assert.Equal("first\n\nsecond", runtime.LastAssistantText);
    }

    [Fact]
    public async Task LastAssistantText_PrefersTheDriversOwnResult_WhenItReportsOne()
    {
        var driver = _DriverEmitting(
            new AssistantTextCompleted { SessionId = "s1", Text = "streamed prose" },
            new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "the final result", IsError = false });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime, expectedEvents: 2);

        Assert.Equal("the final result", runtime.LastAssistantText);
    }

    [Fact]
    public async Task EventsSince_ReplaysFromTheStart_SoAConsumerThatAttachedLateMissesNothing()
    {
        // This is why the runtime keeps a log rather than only raising an event: a delegated task subscribes
        // after the session was started, and would otherwise never see the events it missed.
        var driver = _DriverEmitting(
            new AssistantTextCompleted { SessionId = "s1", Text = "one" },
            new AssistantTextCompleted { SessionId = "s1", Text = "two" });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime, expectedEvents: 2);

        var (events, cursor) = runtime.EventsSince(0);

        Assert.Equal(2, events.Count);
        Assert.Equal(2, cursor);

        var (afterCursor, nextCursor) = runtime.EventsSince(cursor);
        Assert.Empty(afterCursor);
        Assert.Equal(2, nextCursor);
    }

    [Fact]
    public async Task DisposeAsync_InterruptsTheTurnAndDisposesTheDriver()
    {
        var driver = _DriverEmitting();
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);
        await runtime.StartAsync(profile: null);

        await runtime.DisposeAsync();

        await driver.Received(1).InterruptAsync(Arg.Any<CancellationToken>());
        await driver.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task LiveControls_GoStraightToTheDriver()
    {
        var driver = _DriverEmitting();
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);
        await runtime.StartAsync(profile: null);

        await runtime.SetModelAsync("opus");
        await runtime.SendUserMessageAsync("hi");

        await driver.Received(1).SetModelAsync("opus", Arg.Any<CancellationToken>());
        await driver.Received(1).SendUserMessageAsync("hi", Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenItsProcessDiesOutOfBand_IsRunningSaysSo_WithoutADispose()
    {
        // AC-693: a crash, or a kill from outside this class (AC-661's OS cap, AC-692's button), ends the driver's
        // event stream and nothing else — no exception, no DisposeAsync. IsRunning used to read `_pump is not null`,
        // which only ever went false in DisposeAsync, so a dead session kept reporting itself alive and the next send
        // went into a stdin pipe with no reader ("The pipe is being closed.").
        var processDied = new TaskCompletionSource();
        var driver = _DriverDyingOn(processDied.Task, new AssistantTextCompleted { SessionId = "s1", Text = "alive" });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime, expectedEvents: 1);
        Assert.True(runtime.IsRunning);

        processDied.SetResult();

        await _UntilAsync(
            () => !runtime.IsRunning,
            "the runtime still reported itself running 5s after its process died");
    }

    private static async Task _UntilAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(timeoutMessage);
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Waits until the runtime has consumed <paramref name="expectedEvents"/> events.
    /// </summary>
    /// <remarks>
    /// This used to wait for the <em>first</em> event and then sleep a flat 20ms for whatever else was coming —
    /// a guess, not a wait. Under load the later events had not landed yet, so a test expecting a folded
    /// "first\n\nsecond" saw only "first", and the suite failed about one run in eight. That is the kind of
    /// flake that teaches you to re-run instead of read, and a suite you re-run past is one that will hide a
    /// real failure. Waiting for the count the test actually expects removes the guess; the timeout turns a hang
    /// into a message that says what arrived.
    /// </remarks>
    private static async Task _DrainAsync(SessionRuntime runtime, int expectedEvents)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (runtime.EventsSince(0).Events.Count < expectedEvents)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The runtime consumed {runtime.EventsSince(0).Events.Count} of {expectedEvents} events within 5s.");
            }

            await Task.Delay(5);
        }
    }

    // A live driver: it emits its events and then keeps the stream open, because its process is still there with
    // nothing more to say yet. A stream that ends is a process that died, which is what _DriverDyingOn is for.
    private static ISessionDriver _DriverEmitting(params SessionEvent[] events) =>
        _DriverDyingOn(new TaskCompletionSource().Task, events);

    private static ISessionDriver _DriverDyingOn(Task processDied, params SessionEvent[] events)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_ => _Stream(events, processDied));
        return driver;
    }

    private static async IAsyncEnumerable<SessionEvent> _Stream(
        SessionEvent[] events,
        Task processDied,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }

        // Stdout's EOF, as the pump sees it: the loop simply ends, with no exception and nothing disposed.
        await processDied.WaitAsync(cancellationToken);
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
