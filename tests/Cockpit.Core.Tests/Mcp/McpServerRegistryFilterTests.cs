using Cockpit.Core.Mcp;
using FluentAssertions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpServerRegistryFilter.ApplySessionSelection"/>: the per-session MCP-server selection (#44)
/// narrows the registry to the given names, and a <see langword="null"/> selection is a no-op pass-through.
/// </summary>
public class McpServerRegistryFilterTests
{
    private static readonly McpServerConfig ServerA = new() { Name = "server-a", Command = "npx" };
    private static readonly McpServerConfig ServerB = new() { Name = "server-b", Command = "npx" };

    // An internal-only endpoint (AC-204, the Autopilot CEO/step tools): hosted and mountable, but hidden from every
    // user-facing selection and the no-selection fan-out.
    private static readonly McpServerConfig InternalServer = new() { Name = "cockpit-autopilot-ceo", Url = "http://127.0.0.1:1/mcp", Internal = true };

    [Fact]
    public void ApplySessionSelection_WithNullSelection_ReturnsTheFullRegistry()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], enabledServerNames: null);

        result.Should().Equal(ServerA, ServerB);
    }

    [Fact]
    public void ApplySessionSelection_WithNullSelection_DropsInternalEndpoints_FromTheAllEnabledFanOut()
    {
        // No selection means "every enabled server", but an internal-only endpoint (AC-204) must never fan into a
        // session that did not name it — an unrelated no-selection session started while an Autopilot run is live
        // must not inherit the CEO/step tools. Red without the fix, which returned the registry verbatim here.
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, InternalServer, ServerB], enabledServerNames: null);

        result.Should().Equal(ServerA, ServerB);
    }

    [Fact]
    public void ApplySessionSelection_WithAnExplicitSelectionNamingAnInternalEndpoint_StillMountsIt()
    {
        // The autopilot mount: the run's CEO/step sessions scope their MCP servers to the endpoint by name
        // (McpServers = [AutopilotCeoTools.EndpointName]), so an explicit selection must keep reaching it even
        // though the no-selection path hides it.
        var result = McpServerRegistryFilter.ApplySessionSelection(
            [ServerA, InternalServer, ServerB], new HashSet<string> { InternalServer.Name });

        result.Should().ContainSingle().Which.Should().Be(InternalServer);
    }

    [Fact]
    public void ApplySessionSelection_WithASelection_KeepsOnlyTheNamedServers()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], new HashSet<string> { "server-a" });

        result.Should().ContainSingle().Which.Should().Be(ServerA);
    }

    [Fact]
    public void ApplySessionSelection_WithAnEmptySelection_DropsEveryEnabledRegistryServer()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], new HashSet<string>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplySessionSelection_KeepsAnAlreadyDisabledServer_EvenWhenNotInTheSelection()
    {
        var disabled = ServerB with { Enabled = false };

        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, disabled], new HashSet<string> { "server-a" });

        // The checklist only ever offers enabled registry servers, so a disabled one — e.g. one that
        // deliberately overrides and suppresses a local-model built-in default (#26) — was never a
        // checkbox the operator could uncheck, and must keep passing through untouched.
        result.Should().Equal(ServerA, disabled);
    }

    [Fact]
    public void EffectiveSessionSelection_WithAnExplicitSessionSelection_UsesIt_IgnoringTheProfile()
    {
        var result = McpServerRegistryFilter.EffectiveSessionSelection(
            new HashSet<string> { "server-a" }, profileSelection: ["server-b"]);

        result.Should().BeEquivalentTo(["server-a"]);
    }

    [Fact]
    public void EffectiveSessionSelection_WithNoSessionSelection_FallsBackToTheProfilesSavedSelection()
    {
        // The gap this closes: a programmatic launch (a plugin/workflow shortcut, a restored session) carries no
        // dialog-built selection, so without the fallback it would reach every enabled server instead of the
        // profile's checklist (#44/AC-130).
        var result = McpServerRegistryFilter.EffectiveSessionSelection(sessionSelection: null, profileSelection: ["server-b"]);

        result.Should().BeEquivalentTo(["server-b"]);
    }

    [Fact]
    public void EffectiveSessionSelection_WithNeither_IsNull_MeaningNoRestriction()
    {
        McpServerRegistryFilter.EffectiveSessionSelection(sessionSelection: null, profileSelection: null).Should().BeNull();
    }

    [Fact]
    public void EffectiveSessionSelection_AnEmptySessionSelection_IsHonoured_NotOverriddenByTheProfile()
    {
        // An explicit empty selection is a real "these none" choice, distinct from the absence a programmatic
        // launch has — so it must win over the profile's set rather than fall back to it.
        var result = McpServerRegistryFilter.EffectiveSessionSelection(new HashSet<string>(), profileSelection: ["server-b"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void EffectiveSessionSelection_WithNoSessionSelection_AndAnEmptyProfileSelection_RestrictsToNone()
    {
        // An explicit empty profile list is a real "restrict to none" (the mirror of the empty-session case), not
        // the absence that falls back to "all" — so a programmatic launch under such a profile gets zero servers.
        var result = McpServerRegistryFilter.EffectiveSessionSelection(sessionSelection: null, profileSelection: []);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void EffectiveSessionSelection_TheProfileFallback_MatchesServerNamesCaseInsensitively()
    {
        // The fallback set is built OrdinalIgnoreCase, so a saved name resolves against the catalog regardless of
        // case — the same comparison ApplySessionSelection relies on. A regression to an ordinal comparer would
        // silently drop a server whose registered casing differs from the profile's saved name.
        var result = McpServerRegistryFilter.EffectiveSessionSelection(sessionSelection: null, profileSelection: ["Server-A"]);

        result.Should().NotBeNull();
        result!.Contains("server-a").Should().BeTrue();
    }
}
