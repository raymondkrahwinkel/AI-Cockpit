using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Backend.Tests.Plugins;

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
    public async Task Register_TwoPluginsMayOfferTheSameAction_AndDispatchStaysAddressed()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "autopilot" }));
        registry.Register("scripted", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "scripted" }));

        Assert.Equal("autopilot", (await registry.Dispatch(Intent("youtrack", "autopilot", "start")))!["who"]);
        Assert.Equal("scripted", (await registry.Dispatch(Intent("youtrack", "scripted", "start")))!["who"]);
    }
}
