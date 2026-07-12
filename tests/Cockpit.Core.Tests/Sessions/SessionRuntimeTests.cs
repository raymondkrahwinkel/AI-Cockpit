using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
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
        await _DrainAsync(runtime);

        seen.Should().HaveCount(2);
        runtime.IsRunning.Should().BeTrue();
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
        await _DrainAsync(runtime);

        runtime.LastAssistantText.Should().Be("first\n\nsecond");
    }

    [Fact]
    public async Task LastAssistantText_PrefersTheDriversOwnResult_WhenItReportsOne()
    {
        var driver = _DriverEmitting(
            new AssistantTextCompleted { SessionId = "s1", Text = "streamed prose" },
            new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "the final result", IsError = false });
        var runtime = new SessionRuntime(_FactoryFor(driver), profile: null);

        await runtime.StartAsync(profile: null);
        await _DrainAsync(runtime);

        runtime.LastAssistantText.Should().Be("the final result");
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
        await _DrainAsync(runtime);

        var (events, cursor) = runtime.EventsSince(0);

        events.Should().HaveCount(2);
        cursor.Should().Be(2);

        var (afterCursor, nextCursor) = runtime.EventsSince(cursor);
        afterCursor.Should().BeEmpty("everything up to the cursor has already been handed out");
        nextCursor.Should().Be(2);
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

    // Waits for the pump to have handled everything the fake driver emitted. The stream completes as soon as it
    // runs out of events, so the runtime's own teardown is what settles it.
    private static async Task _DrainAsync(SessionRuntime runtime)
    {
        for (var attempt = 0; attempt < 50 && runtime.EventsSince(0).Events.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(20);
    }

    private static ISessionDriver _DriverEmitting(params SessionEvent[] events)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_ => _Stream(events));
        return driver;
    }

    private static async IAsyncEnumerable<SessionEvent> _Stream(
        SessionEvent[] events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
