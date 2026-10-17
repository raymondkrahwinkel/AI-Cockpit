using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1568: the backend half of the Depot file browser — every Depot call goes through the connection's MCP server.
public class DepotBrowserServiceTests
{
    private const string Projects = """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"{ROLE}"}]}""";

    // A host whose MCP tools answer from `Tools` (tool name → answer) and which remembers every call.
    private sealed class _Rig
    {
        public Dictionary<string, Func<IReadOnlyDictionary<string, object?>, PluginMcpToolCallResult>> Tools { get; } = new();
        public List<(string Tool, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];
        public ICockpitHost Host { get; } = Substitute.For<ICockpitHost>();
        public DepotBrowserService Service { get; }

        public _Rig(string role = "Editor")
        {
            Tools["list_projects"] = _ => PluginMcpToolCallResult.Success(Projects.Replace("{ROLE}", role));
            Host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var tool = call.ArgAt<string>(1);
                    var arguments = call.ArgAt<IReadOnlyDictionary<string, object?>?>(2) ?? new Dictionary<string, object?>();
                    Calls.Add((tool, arguments));
                    return Task.FromResult(Tools.TryGetValue(tool, out var answer) ? answer(arguments) : PluginMcpToolCallResult.Failed($"no {tool}"));
                });
            Service = new DepotBrowserService(Host, () => [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]);
        }

        public int Count(string tool) => Calls.Count(call => call.Tool == tool);
    }

    private static DepotBrowserRequest _Request(string? path = null, string? mode = null, string? shareId = null) =>
        new("c1", "cockpit", path, mode, shareId);

    [Fact]
    public async Task Tree_ReadsEveryPage_MergesArtifacts_AndMarksSharesFromOneListShares()
    {
        var rig = new _Rig();
        rig.Tools["list"] = arguments => arguments.ContainsKey("after")
            ? PluginMcpToolCallResult.Success("""{"entries":[{"path":"docs/page-two.md","size":9,"updatedAt":"2026-08-29T17:08:18+00:00"}]}""")
            : PluginMcpToolCallResult.Success("""{"entries":[{"path":"docs/page-one.md","size":5}],"nextCursor":"docs/page-one.md"}""");
        rig.Tools["list_artifacts"] = _ => PluginMcpToolCallResult.Success("""{"entries":[{"path":"img/a.png","contentType":"image/png","size":10},{"path":"img/b.png","contentType":"image/png","size":11}]}""");
        rig.Tools["list_shares"] = _ => PluginMcpToolCallResult.Success("""{"shares":[{"shareId":"s1","path":"img/b.png","isPublic":true,"url":"https://d/artifact/p/s1"}]}""");

        var answer = await rig.Service.TreeAsync(_Request(), default);

        var nodes = answer.Data!.Nodes;
        Assert.Equal(["docs/page-one.md", "docs/page-two.md", "img/a.png", "img/b.png"], nodes.Select(node => node.Path));
        Assert.Equal(DepotBrowserKind.Document, nodes[1].Kind);
        Assert.Equal(DepotBrowserKind.Artifact, nodes[2].Kind);
        Assert.Null(nodes[2].ShareId);
        Assert.Equal(("s1", true), (nodes[3].ShareId, nodes[3].IsPublic));
        Assert.Equal(1, rig.Count("list_shares"));
        Assert.True(answer.Data.CanShare);
    }

    [Fact]
    public async Task Share_PassesTheMode_ButOnlyForAnEditor_AndUnshareRevokesByShareId()
    {
        var editor = new _Rig("Editor");
        editor.Tools["share_artifact"] = _ => PluginMcpToolCallResult.Success("""{"shareId":"s9","url":"https://d/artifact/p/s9","isPublic":true}""");
        editor.Tools["unshare_artifact"] = _ => PluginMcpToolCallResult.Success("{}");

        var shared = await editor.Service.ShareAsync(_Request("img/a.png", "public"), default);
        var revoked = await editor.Service.UnshareAsync(_Request(shareId: "s9"), default);

        Assert.Equal(new DepotBrowserShare("s9", "https://d/artifact/p/s9", true), shared.Data);
        Assert.Equal("public", editor.Calls.Single(call => call.Tool == "share_artifact").Arguments["mode"]);
        Assert.Equal("s9", editor.Calls.Single(call => call.Tool == "unshare_artifact").Arguments["shareId"]);
        Assert.True(revoked.Data);

        var viewer = new _Rig("Viewer");
        var refusedShare = await viewer.Service.ShareAsync(_Request("img/a.png", "members"), default);
        var refusedUnshare = await viewer.Service.UnshareAsync(_Request(shareId: "s9"), default);

        Assert.Equal(DepotBrowserStatus.Error, refusedShare.Status);
        Assert.Equal(DepotBrowserStatus.Error, refusedUnshare.Status);
        Assert.Equal(0, viewer.Count("share_artifact") + viewer.Count("unshare_artifact"));
    }

    [Theory]
    [InlineData(PluginMcpToolCallOutcome.AuthorizationRequired, DepotBrowserStatus.SignIn, "Sign in to Work")]
    [InlineData(PluginMcpToolCallOutcome.Failed, DepotBrowserStatus.Error, "[Forbidden] no access to cockpit")]
    public async Task Tree_NotSignedInOrRefused_SaysSoWithoutSigningIn(PluginMcpToolCallOutcome outcome, string status, string message)
    {
        var rig = new _Rig();
        rig.Tools["list"] = _ => outcome == PluginMcpToolCallOutcome.AuthorizationRequired
            ? PluginMcpToolCallResult.AuthorizationRequired
            : PluginMcpToolCallResult.Failed("[Forbidden] no access to cockpit");

        var answer = await rig.Service.TreeAsync(_Request(), default);

        Assert.Equal((status, message), (answer.Status, answer.Message));
        _ = rig.Host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
