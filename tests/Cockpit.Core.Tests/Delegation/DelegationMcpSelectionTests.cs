using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// AC-133 (completing AC-130 in the delegation path): the MCP servers a delegated session receives are narrowed
/// to the profile's own saved pre-selection when it has one — a delegated session never opens the New-session
/// dialog that would otherwise apply it, so without this it would reach every enabled server. A null selection
/// keeps the prior "all enabled" behaviour, and the orchestrator-removal rule (recursion guard) still applies on
/// top.
/// </summary>
public class DelegationMcpSelectionTests
{
    private const string Orchestrator = "cockpit-orchestrator";

    [Fact]
    public async Task NullSelection_KeepsEveryEnabledServer_MinusTheOrchestrator()
    {
        var service = _ServiceWithRegistry(
            _Enabled("filesystem"), _Enabled("youtrack"), _Enabled(Orchestrator), _Disabled("git"));

        var servers = await service._ToolsForAsync(_Profile(selection: null));

        servers.Should().BeEquivalentTo("filesystem", "youtrack");
    }

    [Fact]
    public async Task ASelection_NarrowsToItsIntersectionWithTheEnabledServers()
    {
        var service = _ServiceWithRegistry(
            _Enabled("filesystem"), _Enabled("youtrack"), _Enabled("git"));

        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem", "git"]));

        servers.Should().BeEquivalentTo("filesystem", "git");
    }

    [Fact]
    public async Task ASelection_DropsNamesTheRegistryNoLongerEnables_SoItCannotWiden()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Disabled("youtrack"));

        // "youtrack" is disabled and "ghost" unknown — a stale saved selection must not reach either.
        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem", "youtrack", "ghost"]));

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task TheIntersectionIsCaseInsensitive()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"));

        var servers = await service._ToolsForAsync(_Profile(selection: ["FileSystem"]));

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task AnEmptySelection_MeansNone()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled("youtrack"));

        var servers = await service._ToolsForAsync(_Profile(selection: []));

        servers.Should().BeEmpty();
    }

    [Fact]
    public async Task TheOrchestratorRemovalAppliesOnTopOfTheSelection()
    {
        // The profile selected the orchestrator, but with no MayDelegateFurther the recursion guard still strips it.
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled(Orchestrator));

        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem", Orchestrator]));

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task AMayDelegateFurtherProfile_KeepsTheOrchestratorWhenSelected()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled(Orchestrator));

        var servers = await service._ToolsForAsync(
            _Profile(selection: ["filesystem", Orchestrator], mayDelegateFurther: true));

        servers.Should().BeEquivalentTo("filesystem", Orchestrator);
    }

    [Fact]
    public async Task AMayDelegateFurtherProfile_StillLosesTheOrchestratorWhenTheSelectionExcludesIt()
    {
        // Even when delegation is allowed, a selection that leaves the orchestrator out drops it — the narrowing
        // beats the permission, because the intersection removes it before the (skipped) guard would have.
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled(Orchestrator));

        var servers = await service._ToolsForAsync(
            _Profile(selection: ["filesystem"], mayDelegateFurther: true));

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task APerTaskSelection_NarrowsWithinTheProfileSelection()
    {
        // AC-136: the per-task layer intersects on top of the profile selection — the effective set is what the
        // task asked for, bounded by what the profile allows.
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled("youtrack"), _Enabled("git"));

        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem", "youtrack"]), ["filesystem"]);

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task APerTaskSelection_CannotWidenPastWhatIsAllowed()
    {
        // A per-task name the profile selection excludes is dropped by the intersection — the pure layer can only
        // narrow. (DelegateAsync additionally refuses such an escalation up front; see DelegationPerTaskMcpTests.)
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled("git"));

        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem"]), ["filesystem", "git"]);

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task APerTaskSelectionOfTheOrchestrator_IsStillDroppedWithoutMayDelegateFurther()
    {
        // The recursion guard applies to the per-task layer too: even naming the orchestrator per task cannot get
        // it past the !MayDelegateFurther removal.
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled(Orchestrator));

        var servers = await service._ToolsForAsync(
            _Profile(selection: ["filesystem", Orchestrator]), [Orchestrator, "filesystem"]);

        servers.Should().BeEquivalentTo("filesystem");
    }

    [Fact]
    public async Task APerTaskSelectionOfTheOrchestrator_IsKeptWithMayDelegateFurther()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled(Orchestrator));

        var servers = await service._ToolsForAsync(
            _Profile(selection: ["filesystem", Orchestrator], mayDelegateFurther: true), [Orchestrator]);

        servers.Should().BeEquivalentTo(Orchestrator);
    }

    private static McpServerConfig _Enabled(string name) => new() { Name = name, Enabled = true };

    private static McpServerConfig _Disabled(string name) => new() { Name = name, Enabled = false };

    private static SessionProfile _Profile(IReadOnlyList<string>? selection, bool mayDelegateFurther = false) =>
        new("local", new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, MayDelegateFurther: mayDelegateFurther))
        {
            EnabledMcpServerNames = selection,
        };

    private static DelegationService _ServiceWithRegistry(params McpServerConfig[] registry)
    {
        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(registry);

        return new DelegationService(
            Substitute.For<ISessionProfileStore>(),
            Substitute.For<ISessionManager>(),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMinutes(minutes));
    }
}
