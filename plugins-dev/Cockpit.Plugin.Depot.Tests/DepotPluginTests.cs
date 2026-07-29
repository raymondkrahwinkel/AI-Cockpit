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
    [Fact]
    public void Initialize_RegistersTheDepotMemorySource_OnTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        ProjectMemorySourceRegistration? registered = null;
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registered = call.Arg<ProjectMemorySourceRegistration>());

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.NotNull(registered);
        Assert.Equal("depot", registered!.Scheme);
        Assert.Contains("Depot MCP", registered.Instruction);
        Assert.Contains("say so rather than working from memory you cannot see", registered.Instruction);
    }

    [Fact]
    public void Metadata_Always_MatchesTheManifestTheHostLoadsBy()
    {
        Assert.Equal("depot", new DepotPlugin().Metadata.Id);
    }
}
