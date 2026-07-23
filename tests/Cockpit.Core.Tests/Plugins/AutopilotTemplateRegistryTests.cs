using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
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

        var registration = registry.Registrations.Should().ContainSingle().Subject;
        registration.OwnerPluginId.Should().Be("autopilot");
        registration.Template.Id.Should().Be("autopilot.triage");
        registration.Template.Body.Should().Be("Triage {{issue.id}}");
    }

    [Fact]
    public void Register_SameTemplateIdFromOnePlugin_Replaces_RatherThanDoubles()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("acme", Template("acme.brief", "First", "one"));
        registry.Register("acme", Template("acme.brief", "Second", "two"));

        var registration = registry.Registrations.Should().ContainSingle().Subject;
        registration.Template.Name.Should().Be("Second");
        registration.Template.Body.Should().Be("two");
    }

    [Fact]
    public void Register_SameTemplateIdFromDifferentPlugins_AreKeptApart()
    {
        var registry = new AutopilotTemplateRegistry();

        registry.Register("acme", Template("brief"));
        registry.Register("globex", Template("brief"));

        registry.Registrations.Select(registration => registration.OwnerPluginId).Should().BeEquivalentTo("acme", "globex");
    }

    [Fact]
    public void Host_RegisterAutopilotTemplate_RoutesToTheRegistry_StampingThisPluginsId()
    {
        var registry = new AutopilotTemplateRegistry();
        var services = new ServiceCollection().AddSingleton<IAutopilotTemplateRegistry>(registry).BuildServiceProvider();
        ICockpitHost host = NewHost("acme", services);

        host.RegisterAutopilotTemplate(Template("acme.triage", "Triage", "body"));

        host.RegisteredAutopilotTemplates.Should().ContainSingle()
            .Which.OwnerPluginId.Should().Be("acme"); // stamped from the host's own id, not composed by the caller
        registry.Registrations.Should().ContainSingle();
    }

    // The defaults are a no-op, so a plugin built against this SDK still loads on a host that predates the
    // contribution point instead of failing at registration.
    [Fact]
    public void AHostWithoutTheContributionPoint_AcceptsTheRegistration_AndReportsNoTemplates()
    {
        ICockpitHost host = new OlderHost();

        var register = () => host.RegisterAutopilotTemplate(Template("x"));

        register.Should().NotThrow();
        host.RegisteredAutopilotTemplates.Should().BeEmpty();
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
            NullCockpitSessionObserver.Instance);
}
