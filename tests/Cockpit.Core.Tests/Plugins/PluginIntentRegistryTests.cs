using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

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

        result.Should().NotBeNull();
        result!["session"].Should().Be("pane-AC-95");
    }

    [Fact]
    public async Task Dispatch_ReturnsNull_WhenNoHandlerIsRegistered()
    {
        var registry = new PluginIntentRegistry();

        var result = await registry.Dispatch(Intent("youtrack", "autopilot", "start"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_ReturnsNull_WhenThePluginHandlesAnotherActionButNotThisOne()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var result = await registry.Dispatch(Intent("youtrack", "autopilot", "stop"));

        result.Should().BeNull();
    }

    [Fact]
    public void HasHandler_ReflectsExactlyWhatIsRegistered()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        registry.HasHandler("autopilot", "start").Should().BeTrue();
        registry.HasHandler("autopilot", "stop").Should().BeFalse();
        registry.HasHandler("something-else", "start").Should().BeFalse();
    }

    [Fact]
    public void Register_Throws_WhenOnePluginClaimsTheSameActionTwice()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var act = () => registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public async Task Register_TwoPluginsMayOfferTheSameAction_AndDispatchStaysAddressed()
    {
        var registry = new PluginIntentRegistry();
        registry.Register("autopilot", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "autopilot" }));
        registry.Register("scripted", "start", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["who"] = "scripted" }));

        (await registry.Dispatch(Intent("youtrack", "autopilot", "start")))!["who"].Should().Be("autopilot");
        (await registry.Dispatch(Intent("youtrack", "scripted", "start")))!["who"].Should().Be("scripted");
    }
}
