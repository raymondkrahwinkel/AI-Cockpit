using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Confinement of a session's MCP tools to a worktree (AC-174, Raymond 2026-07-22): the security-critical transform that
/// lets a local model run an isolated step without reaching the operator's real checkout. It re-roots the filesystem
/// server at the worktree and drops every server that could write or execute outside it.
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
        // Escape channels that must be dropped.
        new() { Name = "cockpit-terminal", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        new() { Name = "cockpit-worktrees", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        // The pane-scoped report endpoint the step legitimately needs.
        new() { Name = "cockpit-autopilot-run", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
    ];

    [Fact]
    public void ConfinedServers_ReRootsFilesystem_AtTheWorktree_FromThePresetNotTheInput()
    {
        var confined = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1");

        var filesystem = Assert.Single(confined, server => server.Name == "filesystem");
        // Its allowed directory (the last arg) is the worktree, not the operator's home.
        Assert.Equal("/wt/run-1", filesystem.Args[^1]);
        Assert.DoesNotContain("/home/op", filesystem.Args);
    }

    [Fact]
    public void ConfinedServers_DropsEveryEscapeChannel()
    {
        var names = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1").Select(server => server.Name).ToList();

        Assert.DoesNotContain("cockpit-terminal", names);
        Assert.DoesNotContain("cockpit-orchestrator", names);
        Assert.DoesNotContain("cockpit-worktrees", names);
        Assert.DoesNotContain("fetch", names);
        Assert.DoesNotContain("git", names);
    }

    [Fact]
    public void ConfinedServers_KeepsMemory_AndTheReportEndpoint()
    {
        var names = McpToolProvider._ConfinedServers(_FullEffectiveSet(), "/wt/run-1").Select(server => server.Name).ToList();

        Assert.Contains("memory", names);
        Assert.Contains("cockpit-autopilot-run", names);
    }

    [Fact]
    public void ConfinedServers_WithoutAReportEndpoint_StillYieldsOnlyTheSafeFileServers()
    {
        // A session that never had the report endpoint does not gain it — confinement never adds reach it lacked.
        IReadOnlyList<McpServerConfig> noReport =
        [
            _Stdio("filesystem", "-y", "@modelcontextprotocol/server-filesystem", "/home/op"),
            new() { Name = "cockpit-terminal", Transport = McpTransport.Http, Url = "http://x", CockpitHosted = true },
        ];

        var names = McpToolProvider._ConfinedServers(noReport, "/wt/run-1").Select(server => server.Name).ToList();

        Assert.Equivalent(new object[] { "filesystem", "memory" }, names);
    }
}
