using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotPlugin"/>'s <c>Initialize</c> — the only thing this plugin does at runtime. Asserts on the
/// registration's content, not merely that <c>AddProjectMemorySource</c> was called: a call with the wrong scheme
/// or a blank instruction would still "pass" a test that only checked the call happened.
/// </summary>
public class DepotPluginTests
{
    private static ICockpitHost _HostWithConnections(params DepotConnectionRegistration[] connections)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        if (connections.Length > 0)
        {
            new Settings.DepotSettings(host.Storage) { Connections = connections };
        }

        return host;
    }

    [Fact]
    public void Initialize_NoConnectionsConfigured_RegistersNoMemorySource()
    {
        // Acceptance criterion 5: the row behaves exactly as it did before this plugin existed when nothing is
        // configured, rather than always offering a fixed "Depot project" nothing points at.
        var host = _HostWithConnections();

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
    }

    [Fact]
    public void Initialize_OneConnectionConfigured_RegistersItUnderThePlainDepotScheme()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("c1", "Synvolution", "https://depot.example.com"));
        var registered = new List<ProjectMemorySourceRegistration>();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registered.Add(call.Arg<ProjectMemorySourceRegistration>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        var registration = Assert.Single(registered);
        Assert.Equal("depot", registration.Scheme);
        Assert.Contains("Synvolution", registration.Title);
        Assert.Contains("Depot MCP", registration.Instruction);
        Assert.Contains("say so rather than working from memory you cannot see", registration.Instruction);
    }

    [Fact]
    public void Initialize_TwoConnectionsConfigured_RegistersBothUnderDistinctSchemes()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Synvolution", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));
        var registered = new List<ProjectMemorySourceRegistration>();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registered.Add(call.Arg<ProjectMemorySourceRegistration>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.Equal(2, registered.Count);
        Assert.Equal("depot", registered[0].Scheme);
        Assert.Equal("depot.wispslate", registered[1].Scheme);
    }

    [Fact]
    public void Metadata_Always_MatchesTheManifestTheHostLoadsBy()
    {
        Assert.Equal("depot", new DepotPlugin().Metadata.Id);
    }
}
