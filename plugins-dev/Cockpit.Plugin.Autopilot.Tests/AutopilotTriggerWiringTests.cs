using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-150/AC-151 start wiring: the plugin registers a "start" intent handler that records the point, surfaces the
/// Autopilot workspace, runs the scoping judgment and either advances the run or parks it. Exercised through the same
/// host contract a tracker's "Start in Autopilot" button uses.
/// </summary>
public class AutopilotTriggerWiringTests
{
    [Fact]
    public async Task StartIntent_WithNoScopingProfile_OpensTheWorkspace_AndReportsStarted()
    {
        var host = Substitute.For<ICockpitHost>();
        host.OpenWorkspaceAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await _RunStartHandler(host, new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-151",
            ["title"] = "trigger",
        });

        await host.Received(1).OpenWorkspaceAsync("workspace.autopilot");
        result.Should().Contain(new KeyValuePair<string, string>("status", "started"));
        result.Should().Contain(new KeyValuePair<string, string>("issue", "AC-151"));
    }

    [Fact]
    public async Task StartIntent_WhenScopingRefuses_ParksTheRun_WithTheReason()
    {
        var host = Substitute.For<ICockpitHost>();
        host.OpenWorkspaceAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        host.Storage.Get<string>("scopingProfileLabel").Returns("scoper");
        host.Actions.DelegateAsync("scoper", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>())
            .Returns("REFUSE: no clear acceptance");

        var result = await _RunStartHandler(host, new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-151",
            ["title"] = "vague point",
        });

        result.Should().Contain(new KeyValuePair<string, string>("status", "refused"));
        result.Should().Contain(new KeyValuePair<string, string>("reason", "no clear acceptance"));
    }

    [Fact]
    public async Task StartIntent_WhenScopingErrors_FailsOpen_AndStarts()
    {
        var host = Substitute.For<ICockpitHost>();
        host.OpenWorkspaceAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        host.Storage.Get<string>("scopingProfileLabel").Returns("scoper");
        host.Actions.DelegateAsync("scoper", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>())
            .Returns<string>(_ => throw new InvalidOperationException("no such profile"));

        var result = await _RunStartHandler(host, new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-151",
            ["title"] = "point",
        });

        result.Should().Contain(new KeyValuePair<string, string>("status", "started"));
    }

    private static async Task<IReadOnlyDictionary<string, string>> _RunStartHandler(ICockpitHost host, IReadOnlyDictionary<string, string> data)
    {
        Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>? registered = null;
        host.When(h => h.RegisterIntentHandler("start", Arg.Any<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>()))
            .Do(call => registered = call.Arg<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>());

        using var plugin = new AutopilotPlugin();
        plugin.Initialize(host);

        var handler = registered ?? throw new InvalidOperationException("The plugin did not register a 'start' intent handler.");
        return await handler(new PluginIntent("youtrack", "autopilot", "start", data));
    }
}
