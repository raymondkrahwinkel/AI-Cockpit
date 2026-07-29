using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The host-owned intent registry (AC-95): a plugin registers a handler for an action, another reaches it by
/// (plugin id, action). Absence is normal — an unaddressed target is a null dispatch, not a throw — but a plugin
/// claiming one action twice is a bug the registry refuses, the same way the workflow step registry refuses a
/// duplicate type id.
/// </summary>
public class PluginIntentRegistryTests
{
    private static PluginIntent Intent(string caller, string target, string action, params (string, string)[] data) =>
        new(caller, target, action, data.ToDictionary(pair => pair.Item1, pair => pair.Item2));

    [Fact]
    public async Task Dispatch_InvokesTheRegisteredHandler_AndReturnsItsResult()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", intent =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["session"] = "pane-" + intent.Data["issue"] }));

        var result = await registry.Dispatch(Intent("youtrack", "autopilot", "start", ("issue", "AC-95")));

        Assert.NotNull(result);
        Assert.Equal("pane-AC-95", result!["session"]);
    }

    [Fact]
    public async Task Dispatch_ReturnsNull_WhenNoHandlerIsRegistered()
    {
        var registry = new PluginIntentRegistry();

        var result = await registry.Dispatch(Intent("youtrack", "autopilot", "start"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Dispatch_ReturnsNull_WhenThePluginHandlesAnotherActionButNotThisOne()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var result = await registry.Dispatch(Intent("youtrack", "autopilot", "stop"));

        Assert.Null(result);
    }

    [Fact]
    public void HasHandler_ReflectsExactlyWhatIsRegistered()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        Assert.True(registry.HasHandler("autopilot", "start"));
        Assert.False(registry.HasHandler("autopilot", "stop"));
        Assert.False(registry.HasHandler("something-else", "start"));
    }

    [Fact]
    public void Register_Throws_WhenOnePluginClaimsTheSameActionTwice()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var act = () => registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public async Task Register_TwoPluginsMayOfferTheSameAction_AndDispatchStaysAddressed()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "autopilot" }));
        registry.Register("scripted", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "scripted" }));

        Assert.Equal("autopilot", (await registry.Dispatch(Intent("youtrack", "autopilot", "start")))!["who"]);
        Assert.Equal("scripted", (await registry.Dispatch(Intent("youtrack", "scripted", "start")))!["who"]);
    }
}
