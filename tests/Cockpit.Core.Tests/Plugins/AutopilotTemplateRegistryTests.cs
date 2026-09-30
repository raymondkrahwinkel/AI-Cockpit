using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The host-owned Autopilot template registry (AC-189): a plugin registers a goal/brief template through the host,
/// which stamps the plugin's own id as its owner, and the Autopilot plugin reads them all back. Registrations live
/// only in memory; re-registering the same template id replaces it. The <see cref="ICockpitHost"/> methods default to
/// a no-op so a host that predates the contribution point still loads a plugin that uses it.
/// </summary>
public class AutopilotTemplateRegistryTests
{
    private static PluginAutopilotTemplate Template(string id, string name = "Name", string body = "body") =>
        new(id, name, body);

    [Fact]
    public void Register_SameTemplateIdFromDifferentPlugins_AreKeptApart()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("acme", Template("brief"));
        registry.Register("globex", Template("brief"));

        Assert.Equivalent(new object[] { "acme", "globex" }, registry.Registrations.Select(registration => registration.OwnerPluginId));
    }

    /// <summary>A host that predates the template contribution point: it implements only the older contract and inherits the new members' default no-op.</summary>
    private sealed class OlderHost : PluginCacheTests.HostWithoutCache;

}
