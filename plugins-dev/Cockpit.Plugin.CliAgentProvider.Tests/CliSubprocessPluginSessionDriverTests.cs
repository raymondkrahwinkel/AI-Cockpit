using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// Proves the process-per-turn lifecycle (spawn per turn, resume via the captured thread id,
// interrupt = kill, stderr drained concurrently so it can never deadlock a turn) without
// needing a real, logged-in `codex` CLI.
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

        Assert.Single(events, evt => evt is PluginSessionInitialized);
        Assert.Equal("Hello, world!", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
        Assert.Equal("thread-1", driver.SessionId);
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

        Assert.Equal("sandbox denied write access", Assert.Single(events.OfType<PluginSessionError>()).Message);
        Assert.True(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
    }

    private static Task<List<PluginSessionEvent>> _CollectUntilTurnCompletedAsync(IPluginSessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is PluginTurnCompleted);

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
