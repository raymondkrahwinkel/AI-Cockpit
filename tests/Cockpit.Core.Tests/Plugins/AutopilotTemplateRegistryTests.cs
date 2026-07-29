using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

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
    public void Register_KeepsTheTemplate_StampedWithItsOwner()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("autopilot", Template("autopilot.triage", "Triage", "Triage {{issue.id}}"));

        var registration = Assert.Single(registry.Registrations);
        Assert.Equal("autopilot", registration.OwnerPluginId);
        Assert.Equal("autopilot.triage", registration.Template.Id);
        Assert.Equal("Triage {{issue.id}}", registration.Template.Body);
    }

    [Fact]
    public void Register_SameTemplateIdFromOnePlugin_Replaces_RatherThanDoubles()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("acme", Template("acme.brief", "First", "one"));
        registry.Register("acme", Template("acme.brief", "Second", "two"));

        var registration = Assert.Single(registry.Registrations);
        Assert.Equal("Second", registration.Template.Name);
        Assert.Equal("two", registration.Template.Body);
    }

    [Fact]
    public void Register_SameTemplateIdFromDifferentPlugins_AreKeptApart()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("acme", Template("brief"));
        registry.Register("globex", Template("brief"));

        Assert.Equivalent(new object[] { "acme", "globex" }, registry.Registrations.Select(registration => registration.OwnerPluginId));
    }

    [Fact]
    public void Host_RegisterAutopilotTemplate_RoutesToTheRegistry_StampingThisPluginsId()
    {
        var registry = new AutopilotTemplateRegistry();
        var services = new ServiceCollection().AddSingleton<IAutopilotTemplateRegistry>(registry).BuildServiceProvider();
        ICockpitHost host = NewHost("acme", services);

        host.RegisterAutopilotTemplate(Template("acme.triage", "Triage", "body"));

        Assert.Equal("acme", Assert.Single(host.RegisteredAutopilotTemplates).OwnerPluginId); // stamped from the host's own id, not composed by the caller
        Assert.Single(registry.Registrations);
    }

    // The defaults are a no-op, so a plugin built against this SDK still loads on a host that predates the
    // contribution point instead of failing at registration.
    [Fact]
    public void AHostWithoutTheContributionPoint_AcceptsTheRegistration_AndReportsNoTemplates()
    {
        ICockpitHost host = new OlderHost();

        var register = () => host.RegisterAutopilotTemplate(Template("x"));

        register();
        Assert.Empty(host.RegisteredAutopilotTemplates);
    }

    /// <summary>A host that predates the template contribution point: it implements only the older contract and inherits the new members' default no-op.</summary>
    private sealed class OlderHost : SessionHeaderItemTests.HostWithoutHeaderItems;

    private static ICockpitHost NewHost(string pluginId, IServiceProvider services) =>
        new CockpitHost(
            pluginId,
            pluginId,
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
}
