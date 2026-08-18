using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// AC-927: every launch route reports the servers it really mounted, so the header stops naming the checklist —
/// which never holds an always-mounted endpoint and so read as that endpoint being missing.
/// </summary>
public class SessionMcpMountReportTests
{
    private const string PaneId = "pane-1";

    private static readonly McpServerConfig SessionEndpoint = new()
    {
        Name = "cockpit-session",
        Transport = McpTransport.Http,
        Url = "http://127.0.0.1:8765/mcp",
        CockpitHosted = true,
        AlwaysMounted = true,
    };

    private static readonly McpServerConfig YouTrack =
        new() { Name = "youtrack", Transport = McpTransport.Http, Url = "http://127.0.0.1:9000/mcp" };

    [Fact]
    public async Task TheSdkRoute_ReportsTheAlwaysMountedEndpoint_TheSelectionLeftOut()
    {
        var reported = new List<string>();
        var mounts = new SessionMcpMounts();
        mounts.Reported += (pane, names) => reported.AddRange(names.Select(name => $"{pane}:{name}"));
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, new McpAuthKey(), _Catalog(), mcpMounts: mounts);

        await adapter.StartAsync(
            enabledMcpServerNames: new HashSet<string> { "youtrack" },
            launchOptions: new Dictionary<string, string> { [WellKnownPluginSessionOptions.PaneId] = PaneId });

        Assert.Equal(["pane-1:cockpit-session", "pane-1:youtrack"], reported);
    }

    [Fact]
    public void TheTtyRoute_ReportsTheAlwaysMountedEndpoint_TheSelectionLeftOut()
    {
        var reported = new List<string>();
        var mounts = new SessionMcpMounts();
        mounts.Reported += (_, names) => reported.AddRange(names);
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter(
            "claude", inner, "{}", _Catalog(), mcpMounts: mounts);

        adapter.BuildLaunch(new TtyLaunchContext(
            null,
            new Dictionary<string, string>(),
            "/wd",
            null,
            new Dictionary<string, string> { ["COCKPIT_PANE_ID"] = PaneId })
        {
            EnabledMcpServerNames = new HashSet<string> { "youtrack" },
        });

        Assert.Equal(["cockpit-session", "youtrack"], reported);
    }

    /// <summary>
    /// The local route reports what answered rather than what was selected: a server that failed to connect is
    /// absent from <c>ConnectedServerNames</c>, and the header must not count one that is not there.
    /// </summary>
    [Fact]
    public async Task TheLocalModelRoute_ReportsTheServersThatActuallyConnected()
    {
        var reported = new List<string>();
        var mounts = new SessionMcpMounts();
        mounts.Reported += (_, names) => reported.AddRange(names);
        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([]);
        toolSession.ConnectedServerNames.Returns(["cockpit-session", "cockpit-agents"]);
        toolSession.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider
            .ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(toolSession);
        var chatClientFactory = Substitute.For<IChatClientFactory>();
        chatClientFactory.Create(Arg.Any<ProviderConfig>()).Returns(Substitute.For<IChatClient>());
        var driver = new OpenAiCompatSessionDriver(
            chatClientFactory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance, mounts);

        await driver.StartAsync(
            new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1")),
            launchOptions: new Dictionary<string, string> { [WellKnownPluginSessionOptions.PaneId] = PaneId });

        Assert.Equal(["cockpit-session", "cockpit-agents"], reported);
    }

    private static IMcpServerCatalog _Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig> { SessionEndpoint, YouTrack });
        return catalog;
    }
}
