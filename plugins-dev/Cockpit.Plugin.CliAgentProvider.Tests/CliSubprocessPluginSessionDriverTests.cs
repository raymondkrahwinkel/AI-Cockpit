using Cockpit.Plugin.CliAgentProvider;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CliSubprocessPluginSessionDriver"/> against a <see cref="FakeCliSubprocess"/> (#45 fase B1) —
/// proves the proces-per-turn lifecycle (spawn per turn, resume via the captured thread id, interrupt = kill,
/// stderr drained concurrently so it can never deadlock a turn) without needing a real, logged-in <c>codex</c>
/// CLI (B2).
/// </summary>
public class CliSubprocessPluginSessionDriverTests
{
    private static CliAgentConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    [Fact]
    public async Task SendUserMessage_StreamsAssistantTextDeltas_ThenCompletesTheTurn()
    {
        var fake = new FakeCliSubprocess();
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        await fake.PushStdoutAsync("""{"type":"thread.started","thread_id":"thread-1"}""");
        await fake.PushStdoutAsync("""{"type":"item.completed","item":{"id":"item_0","item_type":"agent_message","text":"Hello, world!"}}""");
        await fake.PushStdoutAsync("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        fake.CompleteStdout();

        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.Should().ContainSingle(evt => evt is PluginSessionInitialized);
        string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)).Should().Be("Hello, world!");
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
        driver.SessionId.Should().Be("thread-1");
    }

    [Fact]
    public async Task SendUserMessage_FirstTurn_SpawnsWithoutResume_AndPassesThePromptAsAnArgument()
    {
        var fake = new FakeCliSubprocess();
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("do the thing");
        fake.CompleteStdout();
        await _CollectUntilTurnCompletedOrEmptyAsync(driver);

        fake.Arguments.Should().NotBeNull();
        fake.Arguments!.Should().NotContain("resume");
        fake.Arguments!.Should().Contain("exec");
        fake.Arguments!.Should().Contain("do the thing");
        fake.WorkingDirectory.Should().Be(_DefaultConfig().WorkingDirectory);
    }

    [Fact]
    public async Task SendUserMessage_SecondTurn_ResumesTheCapturedThreadId()
    {
        var first = new FakeCliSubprocess();
        var second = new FakeCliSubprocess();
        var subprocesses = new Queue<FakeCliSubprocess>([first, second]);
        var driver = new CliSubprocessPluginSessionDriver(() => subprocesses.Dequeue(), _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("first turn");
        await first.PushStdoutAsync("""{"type":"thread.started","thread_id":"thread-42"}""");
        await first.PushStdoutAsync("""{"type":"turn.completed"}""");
        first.CompleteStdout();
        await _CollectUntilTurnCompletedAsync(driver);

        await driver.SendUserMessageAsync("second turn");
        second.CompleteStdout();
        await _CollectUntilTurnCompletedOrEmptyAsync(driver);

        second.Arguments.Should().NotBeNull();
        var resumeIndex = second.Arguments!.ToList().IndexOf("resume");
        resumeIndex.Should().BeGreaterThanOrEqualTo(0);
        second.Arguments![resumeIndex + 1].Should().Be("thread-42");
    }

    [Fact]
    public async Task InterruptAsync_KillsTheCurrentSubprocess_AndReportsAnInterruptedTurn()
    {
        var fake = new FakeCliSubprocess();
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        await fake.PushStdoutAsync("""{"type":"thread.started","thread_id":"thread-1"}""");

        await driver.InterruptAsync();

        var events = await _CollectUntilTurnCompletedAsync(driver);

        fake.Disposed.Should().BeTrue("InterruptAsync has no in-band cancel message for a headless CLI — it must kill the child outright");
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.StopReason.Should().Be("interrupt");
    }

    [Fact]
    public async Task SendUserMessage_TurnFailed_EmitsSessionErrorAndAFailedTurn()
    {
        var fake = new FakeCliSubprocess();
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        await fake.PushStdoutAsync("""{"type":"thread.started","thread_id":"thread-1"}""");
        await fake.PushStdoutAsync("""{"type":"turn.failed","error":{"message":"sandbox denied write access"}}""");
        fake.CompleteStdout();

        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.OfType<PluginSessionError>().Should().ContainSingle().Which.Message.Should().Be("sandbox denied write access");
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task SendUserMessage_ProcessExitsWithoutATurnCompletion_EmitsASyntheticFailedTurn()
    {
        // Simulates a codex crash: the process exits (stdout hits EOF) without ever emitting
        // turn.completed/turn.failed — the driver must still end the turn rather than hang forever.
        var fake = new FakeCliSubprocess { ExitCode = 1 };
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        fake.CompleteStdout(exitCode: 1);

        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.Should().ContainSingle(evt => evt is PluginSessionError);
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task SendUserMessage_DrainsStderrConcurrently_SoABoundedStderrPipeNeverBlocksTheTurn()
    {
        var fake = new FakeCliSubprocess(stderrCapacity: 1);
        var driver = new CliSubprocessPluginSessionDriver(() => fake, _DefaultConfig(), "codex");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");

        // Push more stderr lines than the bounded channel's capacity while the turn is in flight. Without a
        // dedicated concurrent drain task, the write past capacity would block forever — a full pipe
        // deadlocking the "child" (design doc §4).
        var pushStderr = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await fake.PushStderrAsync($"progress {i}");
            }
        });
        var finishedInTime = await Task.WhenAny(pushStderr, Task.Delay(TimeSpan.FromSeconds(3))) == pushStderr;
        finishedInTime.Should().BeTrue("a dedicated stderr-drain task must keep a bounded stderr pipe from blocking the turn");

        await fake.PushStdoutAsync("""{"type":"thread.started","thread_id":"thread-1"}""");
        await fake.PushStdoutAsync("""{"type":"turn.completed"}""");
        fake.CompleteStdout();

        var events = await _CollectUntilTurnCompletedAsync(driver);
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public void BuildArguments_StdinPromptMode_DoesNotAppendThePromptAsAnArgument()
    {
        var config = _DefaultConfig() with { PromptMode = "stdin" };
        var driver = new CliSubprocessPluginSessionDriver(() => new FakeCliSubprocess(), config, "codex");

        var arguments = driver.BuildArguments("do the thing");

        arguments.Should().NotContain("do the thing");
    }

    [Fact]
    public void BuildArguments_IncludesTheConfiguredSandboxModeFlag()
    {
        var config = _DefaultConfig() with { SandboxMode = "workspace-write" };
        var driver = new CliSubprocessPluginSessionDriver(() => new FakeCliSubprocess(), config, "codex");

        var arguments = driver.BuildArguments("hi");

        var sandboxIndex = arguments.ToList().IndexOf("--sandbox");
        sandboxIndex.Should().BeGreaterThanOrEqualTo(0);
        arguments[sandboxIndex + 1].Should().Be("workspace-write");
    }

    private static Task<List<PluginSessionEvent>> _CollectUntilTurnCompletedAsync(IPluginSessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is PluginTurnCompleted);

    // Same collector, used where the fixture never produces a mapped PluginTurnCompleted at all (an empty
    // stdout script) — bounded by a timeout instead of a stop predicate, purely to drain whatever the driver
    // did emit for an argument-building assertion.
    private static async Task<List<PluginSessionEvent>> _CollectUntilTurnCompletedOrEmptyAsync(IPluginSessionDriver driver)
    {
        var events = new List<PluginSessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var evt in driver.Events.WithCancellation(cts.Token))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the fixture never emits turn.completed, so the enumeration only ends via this timeout.
        }

        return events;
    }

    private static async Task<List<PluginSessionEvent>> _CollectUntilAsync(IPluginSessionDriver driver, Func<PluginSessionEvent, bool> until)
    {
        var events = new List<PluginSessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (until(evt))
            {
                break;
            }
        }

        return events;
    }
}
