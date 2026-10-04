using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1418: the operator's click arrives after the backend moved on — the pump took the front run, the CEO revised the
// draft. An action that names what the operator saw changes nothing once that is no longer there.
public class AutopilotChannelTests
{
    [Theory]
    [InlineData(AutopilotChannelContract.QueueRemove, 0, "A")]
    [InlineData(AutopilotChannelContract.QueueMoveDown, 0, "A")]
    [InlineData(AutopilotChannelContract.QueueMoveUp, 1, "B")]
    [InlineData(AutopilotChannelContract.Submit, 0, "first draft")]
    public async Task AnActionOnWhatTheOperatorNoLongerSees_LeavesTheQueueAsItIs(string action, int index, string seenGoal)
    {
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        var queue = new AutopilotRunQueue(storage);
        var plan = new AutopilotPlanController();
        var handlers = new Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>>();
        var channel = Substitute.For<IPluginBackendChannel>();
        channel.Handle(Arg.Any<string>(), Arg.Any<Func<JsonElement, CancellationToken, Task<JsonElement>>>())
            .Returns(call =>
            {
                handlers[call.ArgAt<string>(0)] = call.ArgAt<Func<JsonElement, CancellationToken, Task<JsonElement>>>(1);
                return Substitute.For<IDisposable>();
            });
        var host = Substitute.For<ICockpitHost>();
        host.Channel.Returns(channel);
        using var backend = new AutopilotChannel(host, settings, plan, new AutopilotRunManager(queue, settings), queue, new AutopilotRunHistory(storage), new AutopilotTemplateStore(storage));

        // What the operator saw: A, B and C queued and a first draft on the table. Then the pump took A and the CEO
        // re-emitted the draft, before the click arrived.
        AutopilotPlan[] seen = [_Plan("A"), _Plan("B"), _Plan("C")];
        foreach (var queued in seen)
        {
            queue.Enqueue(queued);
        }

        var shown = _Plan("first draft");
        plan.BeginPlanning(shown);
        Assert.True(queue.TryDequeue(out _));
        plan.UpdatePlan(_Plan("revised draft"));
        var before = JsonSerializer.Serialize(queue.Items);

        object request = action == AutopilotChannelContract.Submit
            ? new SubmitRequest(shown, "run", "C:/work", false, AutopilotMergeMode.Explicit)
            : new QueueRequest(index, seen.Single(candidate => candidate.Goal == seenGoal));
        await handlers[action](AutopilotChannelContract.ToJson(request), CancellationToken.None);

        Assert.Equal(before, JsonSerializer.Serialize(queue.Items));
    }

    private static AutopilotPlan _Plan(string goal) =>
        new(goal, null, [new AutopilotStep("s1", "Build", "Build it", "work", null, "Do the work", null)]);

    private sealed class FakeStorage : IPluginStorage, IPluginCache
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }
}
