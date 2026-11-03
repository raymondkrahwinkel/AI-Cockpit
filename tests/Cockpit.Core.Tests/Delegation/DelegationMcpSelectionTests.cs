using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Delegation;
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

        Assert.Equivalent(new object[] { "filesystem", "youtrack" }, servers);
    }

    [Fact]
    public async Task ASelection_DropsNamesTheRegistryNoLongerEnables_SoItCannotWiden()
    {
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Disabled("youtrack"));

        // "youtrack" is disabled and "ghost" unknown — a stale saved selection must not reach either.
        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem", "youtrack", "ghost"]));

        Assert.Equivalent(new object[] { "filesystem" }, servers);

        // And an empty selection means none, not "no restriction".
        Assert.Empty(await service._ToolsForAsync(_Profile(selection: [])));
    }

    [Fact]
    public async Task APerTaskSelection_CannotWidenPastWhatIsAllowed()
    {
        // A per-task name the profile selection excludes is dropped by the intersection — the pure layer can only
        // narrow. (DelegateAsync additionally refuses such an escalation up front; see DelegationPerTaskMcpTests.)
        var service = _ServiceWithRegistry(_Enabled("filesystem"), _Enabled("git"));

        var servers = await service._ToolsForAsync(_Profile(selection: ["filesystem"]), ["filesystem", "git"]);

        Assert.Equivalent(new object[] { "filesystem" }, servers);
    }

    private static McpServerConfig _Enabled(string name) => new() { Name = name, Enabled = true };

    private static McpServerConfig _Disabled(string name) => new() { Name = name, Enabled = false };

    private static SessionProfile _Profile(IReadOnlyList<string>? selection) =>
        new("local", new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, MayDelegateFurther: false))
        {
            EnabledMcpServerNames = selection,
        };

    private static DelegationService _ServiceWithRegistry(
        McpServerConfig[] registry,
        IMcpServerCatalog? mcpServerCatalog = null)
    {
        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(registry);

        // The service takes the catalog as a deferred lookup (AC-1110), so a test that has one hands it over as one.
        Func<IMcpServerCatalog?>? catalogLookup = mcpServerCatalog is null ? null : () => mcpServerCatalog;

        return new DelegationService(
            Substitute.For<ISessionProfileStore>(),
            Substitute.For<ISessionManager>(),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMinutes(minutes),
            mcpServerCatalog: catalogLookup);
    }

    private static DelegationService _ServiceWithRegistry(params McpServerConfig[] registry) =>
        _ServiceWithRegistry(registry, mcpServerCatalog: null);
}
