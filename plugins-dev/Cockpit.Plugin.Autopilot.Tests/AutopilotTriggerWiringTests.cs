using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-150 trigger wiring: the plugin registers a "start" intent handler that records the run and surfaces the
/// Autopilot workspace. Exercised through the same host contract a tracker's "Start in Autopilot" button uses.
/// </summary>
public class AutopilotTriggerWiringTests
{
    [Fact]
    public async Task StartIntent_OpensTheAutopilotWorkspace_AndReportsStarted()
    {
        var host = Substitute.For<ICockpitHost>();
        Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>? registered = null;
        host.When(h => h.RegisterIntentHandler("start", Arg.Any<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>()))
            .Do(call => registered = call.Arg<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>());
        host.OpenWorkspaceAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        using var plugin = new AutopilotPlugin();
        plugin.Initialize(host);

        var handler = registered ?? throw new InvalidOperationException("The plugin did not register a 'start' intent handler.");
        var result = await handler(new PluginIntent(
            "youtrack",
            "autopilot",
            "start",
            new Dictionary<string, string> { ["tracker"] = "youtrack", ["issue"] = "AC-150", ["title"] = "trigger" }));

        await host.Received(1).OpenWorkspaceAsync("workspace.autopilot");
        result.Should().Contain(new KeyValuePair<string, string>("status", "started"));
        result.Should().Contain(new KeyValuePair<string, string>("issue", "AC-150"));
    }
}
