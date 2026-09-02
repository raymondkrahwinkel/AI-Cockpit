using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The startup pass that moves a pre-AC-403 token onto the id its server carries now — the migration half of
/// "a rename must not cost the sign-in".
/// <para>
/// Only plugin connections that mint their own id need it: a registry row's id is derived from its name, so its
/// token is already found without anything being rewritten. What is asserted here is which servers get offered to
/// the store, because handing it the wrong ones is how a token would be matched to a name that has since come to
/// mean a different server.
/// </para>
/// </summary>
public class McpOAuthTokenAdoptionTests
{
    private static IMcpServerStore _RegistryWith(params McpServerConfig[] servers)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<McpServerConfig>)servers.ToList());
        return store;
    }

    private static IPluginMcpProvider _PluginOffering(params McpServerContribution[] contributions)
    {
        var provider = Substitute.For<IPluginMcpProvider>();
        provider.GetMcpServers().Returns((IReadOnlyList<McpServerContribution>)contributions.ToList());
        return provider;
    }

    private static (McpOAuthTokenAdoption Adoption, IMcpOAuthTokenStore Tokens) _Create(
        IMcpServerStore registry, params IPluginMcpProvider[] plugins)
    {
        var tokens = Substitute.For<IMcpOAuthTokenStore>();
        return (new McpOAuthTokenAdoption(tokens, registry, plugins, NullLogger<McpOAuthTokenAdoption>.Instance), tokens);
    }

    [Fact]
    public async Task RunAsync_LeavesOutAServerWhoseIdItsOwnNameAlreadyDerivesTo()
    {
        // A registry row and a plugin with a fixed name both land on the derived id, and the store finds their
        // tokens by that derivation alone. Offering them here would add nothing and would put a name in the map
        // that a differently-keyed server might also answer to.
        var (adoption, tokens) = _Create(
            _RegistryWith(new McpServerConfig { Name = "corp", Url = "https://corp.example/mcp" }),
            _PluginOffering(new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token")));

        await adoption.RunAsync();

        await tokens.DidNotReceive().AdoptLegacyEntriesAsync(
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenARegistryRowAndAPluginShareAName_TheRegistryRowWins()
    {
        // The same precedence the host's own sign-in resolution applies to that clash, so the migration cannot file
        // a token against a server the sign-in would never reach.
        var (adoption, tokens) = _Create(
            _RegistryWith(new McpServerConfig { Id = "registry-id", Name = "Depot: work", Url = "https://depot.example/mcp" }),
            _PluginOffering(new McpServerContribution("Depot: work", "https://depot.example/mcp") { Id = "connection-id" }));

        await adoption.RunAsync();

        await tokens.Received().AdoptLegacyEntriesAsync(
            Arg.Is<IReadOnlyDictionary<string, string>>(map => map["Depot: work"] == "registry-id"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenAPluginThrowsWhileListingItsServers_StillAdoptsForTheOthers()
    {
        var throwing = Substitute.For<IPluginMcpProvider>();
        throwing.GetMcpServers().Returns(_ => throw new InvalidOperationException("plugin is having a day"));

        var (adoption, tokens) = _Create(
            _RegistryWith(),
            throwing,
            _PluginOffering(new McpServerContribution("Depot: work", "https://depot.example/mcp") { Id = "connection-id" }));

        await adoption.RunAsync();

        await tokens.Received().AdoptLegacyEntriesAsync(
            Arg.Is<IReadOnlyDictionary<string, string>>(map => map["Depot: work"] == "connection-id"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenTheRegistryCannotBeRead_DoesNotThrow()
    {
        // This runs on the launch path. A migration that cannot run costs a sign-in; one that throws would cost the
        // launch, and it is fire-and-forget, so nothing would be there to catch it.
        var registry = Substitute.For<IMcpServerStore>();
        registry.LoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>(_ => throw new IOException("locked"));

        var (adoption, tokens) = _Create(registry);

        await adoption.RunAsync();

        await tokens.DidNotReceive().AdoptLegacyEntriesAsync(
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }
}
