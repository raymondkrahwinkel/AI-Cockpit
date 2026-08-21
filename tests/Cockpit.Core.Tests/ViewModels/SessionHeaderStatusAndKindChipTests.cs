using System.Globalization;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-537: the SDK header's status line no longer names the tool count or the cwd (both said nothing an operator
/// could act on, and the cwd duplicated the folder icon's own tooltip — SessionHeaderBar.axaml), naming the MCP
/// server count instead; and the kind chip resolves a Plugin-provider profile's own name instead of showing the
/// generic "Plugin" placeholder. TTY's own KindLabel ("TTY") is untouched — this all lives in SessionViewModel.
/// </summary>
public class SessionHeaderStatusAndKindChipTests
{
    private static readonly SessionProfile ClaudeCliProfile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task SessionInitialized_NeverMentionsCwd_RegardlessOfMcpCount()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem", "git" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/home/raymond/work", Tools = ["Read", "Write"] });

        Assert.DoesNotContain("cwd", vm.Status, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithMcpServers_NamesTheirCount()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem", "git", "youtrack" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read", "Write", "Bash"] });

        Assert.Equal("Connected (3 MCP servers).", vm.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithOneMcpServer_UsesTheSingularForm()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected (1 MCP server).", vm.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithNoMcpServers_SaysNothingRatherThanZero()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string>());

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected.", vm.Status);
        Assert.DoesNotContain("0", vm.Status, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithNoExplicitMcpSelection_AndNoProfileDefault_SaysNothingRatherThanGuess()
    {
        // Null all the way down (no session selection, no profile default either) is genuinely ambiguous — a
        // programmatic launch's "host's usual selection" is an unknown, not-necessarily-zero count from here.
        var vm = await _StartedVmAsync(enabledMcpServerNames: null, profile: ClaudeCliProfile);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected.", vm.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithNoSessionSelection_FallsBackToTheProfilesSavedSelection()
    {
        // The concrete gap the adversarial review found: a caller (e.g. an embedded launch) passes no explicit
        // selection, but the profile carries its own saved one (AC-130) — the session mounts those servers via
        // PluginSessionDriverAdapter's own EffectiveSessionSelection merge, so the header must count them too
        // rather than reading back "nothing".
        var profile = ClaudeCliProfile with { EnabledMcpServerNames = ["filesystem", "git", "youtrack"] };
        var vm = await _StartedVmAsync(enabledMcpServerNames: null, profile: profile);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected (3 MCP servers).", vm.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_AnExplicitSessionSelection_OverridesTheProfilesSavedOne()
    {
        // An explicit (even smaller) session selection wins outright — EffectiveSessionSelection never merges
        // the two lists together, it picks one or the other.
        var profile = ClaudeCliProfile with { EnabledMcpServerNames = ["filesystem", "git", "youtrack"] };
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem" }, profile: profile);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected (1 MCP server).", vm.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_CountsNamesAsGiven_NotResolvedAgainstTheLiveRegistry()
    {
        // The seeded value counts the names it was handed and re-checks them against no registry; what the
        // session really mounted arrives afterwards, from the launch route itself (AC-927, `SessionMcpMounts`).
        // This name deliberately looks like one of the cockpit's own endpoints: the seed does not special-case it.
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem", "cockpit-session" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Equal("Connected (2 MCP servers).", vm.Status);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// AC-563 criterion 5. Both readings come off <see cref="SessionPanelViewModel.McpServerSelection"/>, so the
    /// shape this rules out is a header saying three and a hover naming two — with nothing on screen to say which
    /// of the two is the session's actual setup. Asserted by counting the listed names against the number in the
    /// line rather than against a literal, so the two stay tied even if either wording changes.
    /// </summary>
    [Fact]
    public async Task TheHoverNamesExactlyTheServersTheStatusLineCounts()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "youtrack", "filesystem", "git" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        var counted = int.Parse(new string([.. vm.Status.Where(char.IsDigit)]), CultureInfo.InvariantCulture);
        // The server block is the tooltip's first paragraph; AC-963 hangs a second one under it saying how those
        // servers' tools reach the model, which is a statement about tools rather than a name in this list.
        var listed = vm.McpServersTooltip.Split("\n\n")[0].Split('\n')[1..];

        Assert.Equal(counted, listed.Length);
        // Sorted, because a list you look a name up in is sorted, and the order a caller happened to build the
        // set in is not an order.
        Assert.Equal(new[] { "filesystem", "git", "youtrack" }, listed);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// AC-963 criterion 6. The status line stays about servers — that is the unit the operator set up — so what
    /// became of the tools those servers brought has to be said somewhere, or a session running in search mode is
    /// indistinguishable from one that preloaded everything. "Nothing reported" stays unsaid rather than being
    /// rendered as a claim, same rule as the unknown selection below.
    /// </summary>
    [Fact]
    public async Task TheHoverSaysWhetherTheToolsArePreloadedOrSearchable()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "youtrack" });

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["read_file", "write_file"] });
        Assert.Contains("2 tools — preloaded", vm.McpServersTooltip, StringComparison.Ordinal);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["read_file", "search_tools", "call_tool"] });
        Assert.Contains("1 tool — searchable", vm.McpServersTooltip, StringComparison.Ordinal);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = [] });
        Assert.DoesNotContain("preloaded", vm.McpServersTooltip, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// AC-563 criterion 6, and the reason this ticket is not just "bind the names": an unknown selection rendered
    /// as an empty list reads as "this session has no MCP servers" — a claim about the world that not being able
    /// to work something out does not support. The status line already declines to guess here (it says
    /// "Connected." with no number); the hover has to decline in the same direction.
    /// </summary>
    [Fact]
    public async Task WithNothingNamedAnywhere_TheHoverSaysTheSelectionIsUnknown_NotThatThereAreNone()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: null, profile: ClaudeCliProfile);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Contains("Not known", vm.McpServersTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("None", vm.McpServersTooltip, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    /// <summary>AC-563 criterion 7: none is a finding and says so, rather than hovering to nothing at all.</summary>
    [Fact]
    public async Task WithAnEmptySelection_TheHoverSaysThereAreNone()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string>());

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        Assert.Contains("None", vm.McpServersTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("Not known", vm.McpServersTooltip, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// AC-563 criterion 8: an agent's <c>set_status</c> line replaces the words in the activity column, and the
    /// server list must not go with them — that would take the hover away exactly while a session is working.
    /// </summary>
    [Fact]
    public async Task AnAgentStatuslineDoesNotTakeTheServerListWithIt()
    {
        var vm = await _StartedVmAsync(enabledMcpServerNames: new HashSet<string> { "filesystem", "git" });
        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = ["Read"] });

        vm.Statusline = "AC-563 — wiring the header hover";

        Assert.Contains("filesystem", vm.McpServersTooltip, StringComparison.Ordinal);
        Assert.Contains("git", vm.McpServersTooltip, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PluginProviderProfile_WithARegisteredName_ShowsItInsteadOfThePlaceholder()
    {
        var registry = new PluginProviderRegistry();
        registry.Register(_Registration("gemini-provider.gemini", "Gemini"));
        var profile = new SessionProfile("default", new PluginProviderConfig("gemini-provider.gemini", "{}"));

        var vm = await _StartedVmAsync(profile: profile, registry: registry);

        Assert.Equal("Gemini", vm.ProviderBadge);
        Assert.Equal("Gemini", vm.KindLabel);
        Assert.True(vm.ShowKindChip);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PluginProviderProfile_WithNothingRegistered_ShowsNoChipAtAll()
    {
        var registry = new PluginProviderRegistry(); // nothing registered under this id
        var profile = new SessionProfile("default", new PluginProviderConfig("unregistered.provider", "{}"));

        var vm = await _StartedVmAsync(profile: profile, registry: registry);

        Assert.Equal(string.Empty, vm.ProviderBadge);
        Assert.Equal("SDK", vm.KindLabel);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClaudeCliProfile_KeepsShowingSdk_UnaffectedByTheRegistry()
    {
        var vm = await _StartedVmAsync(profile: ClaudeCliProfile, registry: new PluginProviderRegistry());

        Assert.Equal(string.Empty, vm.ProviderBadge);
        Assert.Equal("SDK", vm.KindLabel);

        await vm.DisposeAsync();
    }

    private static SessionProviderRegistration _Registration(string providerId, string displayName) => new(
        ProviderId: providerId,
        DisplayName: displayName,
        CreateDriverFactory: _ => throw new NotSupportedException("Not exercised by these header tests."),
        Capabilities: new PluginSessionCapabilities(false, false),
        CreateConfigView: _ => throw new NotSupportedException("Not exercised by these header tests."));

    private static async Task<SessionViewModel> _StartedVmAsync(
        IReadOnlySet<string>? enabledMcpServerNames = null, SessionProfile? profile = null, IPluginProviderRegistry? registry = null)
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(_EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(_FactoryFor(session)), pluginProviderRegistry: registry);

        await vm.StartConfiguredAsync(
            profile ?? ClaudeCliProfile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            enabledMcpServerNames);

        return vm;
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
