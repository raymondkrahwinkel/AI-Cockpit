using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using System.Reflection;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Confinement of a session's MCP tools to a worktree (AC-174, Raymond 2026-07-22): the security-critical transform that
/// lets a local model run an isolated step without reaching the operator's real checkout. It re-roots the filesystem
/// server at the worktree while preserving the operator's MCP selection (AC-1962).
/// </summary>
public class McpToolProviderConfinementTests
{
    private static McpServerConfig _Stdio(string name, params string[] args) =>
        new() { Name = name, Transport = McpTransport.Stdio, Command = "x", Args = args };

    private static IReadOnlyList<McpServerConfig> _FullEffectiveSet() =>
    [
        // A home-rooted filesystem — the primary escape hole.
        _Stdio("filesystem", "-y", "@modelcontextprotocol/server-filesystem", "/home/op"),
        _Stdio("git", "mcp-server-git", "--repository", "/home/op"),
        _Stdio("fetch", "mcp-server-fetch"),
        _Stdio("memory", "-y", "@modelcontextprotocol/server-memory"),
        // AC-1962: operator-selected servers remain available outside the file-tool guarantee.
        new() { Name = "cockpit-terminal", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-worktrees", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        // The pane-scoped report endpoint the step legitimately needs.
        new() { Name = "cockpit-autopilot-run", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-session", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-agents", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-coding", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-elevated", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true, Internal = true },
    ];

    [Fact]
    public void ConfinedServers_ReRootsFilesystem_AtTheWorktree_FromThePresetNotTheInput()
    {
        var confined = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1");

        var filesystem = Assert.Single(confined, server => server.Name == "filesystem");
        // Its allowed directory (the last arg) is the worktree, not the operator's home.
        Assert.Equal("/wt/run-1", filesystem.Args[^1]);
        Assert.DoesNotContain("/home/op", filesystem.Args);
        Assert.Equal(McpServerPresets.LocalDefaults.Single(server => server.Name == "filesystem").Command, filesystem.Command);
        Assert.Contains(McpServerPresets.FilesystemServerPackage, filesystem.Args);
    }

    [Fact]
    public void ConfinedServers_KeepsEverySelectedServer()
    {
        var names = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1").Select(server => server.Name).ToList();

        // AC-1962: confinement changes the filesystem root, never the operator's selection.
        Assert.Equal(_FullEffectiveSet().Select(server => server.Name), names);
    }

    [Fact]
    public async Task ConfinedServers_KeepsSafeCockpitEndpoints_WhileElevationRequiresExplicitSelection()
    {
        var names = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1").Select(server => server.Name).ToList();
        var endpoints = new ServiceCollection().AddInfrastructure()
            .Where(descriptor => descriptor.ServiceType == typeof(CockpitMcpEndpoint))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<CockpitMcpEndpoint>()
            .ToList();
        var session = Assert.Single(endpoints, endpoint => endpoint.ServerName == "cockpit-session");
        var elevated = Assert.Single(endpoints, endpoint => endpoint.ServerName == "cockpit-elevated");
        var sessionTools = session.ToolsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToList();
        var elevatedTools = elevated.ToolsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToList();
        var visibleSelection = new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase);
        var profileSelection = new HashSet<string>(["cockpit-elevated"], StringComparer.OrdinalIgnoreCase);
        var effectiveSelection = McpServerRegistryFilter.WithInternalProfileServers(visibleSelection, profileSelection, _FullEffectiveSet());
        var worktreeRoot = Path.GetFullPath(Path.Combine("test-data", "worktree"));
        var confinedSession = Substitute.For<ISessionHandle>();
        confinedSession.WorkingDirectory.Returns(worktreeRoot);
        confinedSession.WorktreeBranch.Returns("cockpit/test");
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-1").Returns(confinedSession);
        var outside = Path.GetFullPath(Path.Combine(worktreeRoot, "..", "operator-checkout"));
        var runner = Substitute.For<ITrackedCommandRunner>();
        var statusTools = new SessionStatusTools(
            Substitute.For<ISessionLabelSink>(),
            runner,
            new RunTracker(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IWorkspaceAgentCoordinator>(),
            Substitute.For<IAgentMessageInbox>(),
            sessions: sessions);
        var startRunReply = await statusTools.StartRunAsync(outside, "dotnet", session: "pane-1", kind: "light");

        Assert.Contains("memory", names);
        Assert.Contains("cockpit-autopilot-run", names);
        Assert.Contains("cockpit-session", names);
        Assert.Contains("cockpit-agents", names);
        Assert.Contains("cockpit-coding", names);
        Assert.Contains("cockpit-elevated", names); // AC-1962: explicit selection survives confinement.
        Assert.DoesNotContain("run_elevated", sessionTools);
        Assert.Equal(new[] { "run_elevated" }, elevatedTools);
        Assert.True(elevated.Internal);
        Assert.False(elevated.AlwaysMounted);
        Assert.Contains("cockpit-elevated", effectiveSelection!);
        Assert.Contains("outside your working directory", startRunReply);
        _ = runner.DidNotReceive().RunAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConfinedServers_WithoutAReportEndpoint_PreservesOnlyTheSelectedServers()
    {
        // A session that never had the report endpoint does not gain it — confinement never adds reach it lacked.
        IReadOnlyList<McpServerConfig> noReport =
        [
            _Stdio("filesystem", "-y", "@modelcontextprotocol/server-filesystem", "/home/op"),
            new() { Name = "cockpit-terminal", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        ];

        var names = McpToolProvider._ConfinedServers(noReport, "/wt/run-1").Select(server => server.Name).ToList();

        Assert.Equal(new[] { "filesystem", "cockpit-terminal" }, names); // AC-1962: no filtering or extra presets.
        Assert.Empty(McpToolProvider._ConfinedServers([], "/wt/run-1"));
    }
}
