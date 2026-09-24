using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-1389 (F2.1): a plugin's UI part — activated after the backend parts, handed an ICockpitUiHost, and
// recorded with its reason when it cannot load, without taking its backend part down.
public class PluginUiPartTests
{
    private static readonly string _ThisAssembly = Path.GetFileName(typeof(PluginUiPartTests).Assembly.Location);

    [Fact]
    public void InitializeUi_HandsTheUiEntryTypeAnICockpitUiHost_AndPassesOverAPluginWithoutOne()
    {
        var withUi = _Discovered("with-ui", _ThisAssembly, typeof(RecordingUiPart).FullName);
        var backendOnly = _Discovered("backend-only", uiAssembly: null, uiEntryType: null);
        var manager = _Manager(new PluginDiagnostics());
        manager.LoadAndConfigure([withUi, backendOnly], new ServiceCollection(), candidate => new BackendPart(candidate.FolderId));
        var hosts = new Dictionary<string, ICockpitUiHost>();

        _UiManager(manager, new PluginDiagnostics()).InitializeUi(PluginUiManager.ActivateUi, (discovered, _) => hosts[discovered.FolderId] = Substitute.For<ICockpitUiHost>());

        Assert.Equal(["with-ui"], hosts.Keys);
        Assert.Same(hosts["with-ui"], RecordingUiPart.LastHost.Value);
    }

    [Fact]
    public void InitializeUi_WhenTheUiEntryTypeIsNoICockpitPluginUi_RecordsWhy_AndTheBackendPartStaysLoaded()
    {
        var discovered = _Discovered("wrong-ui", _ThisAssembly, typeof(PluginUiPartTests).FullName);
        var diagnostics = new PluginDiagnostics();
        var manager = _Manager(diagnostics);
        var backend = new BackendPart("wrong-ui");
        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => backend);
        manager.Initialize((_, _) => Substitute.For<ICockpitHost>());

        _UiManager(manager, diagnostics).InitializeUi(PluginUiManager.ActivateUi, (_, _) => Substitute.For<ICockpitUiHost>());

        var failure = Assert.Single(diagnostics.Failures);
        Assert.Equal("initialize-ui", failure.Phase);
        Assert.Contains(nameof(ICockpitPluginUi), failure.Error);
        Assert.Equal(1, backend.InitializeCount);
        Assert.Same(discovered, Assert.Single(manager.Loaded));
    }

    // The overgang: one entry type that is both parts gets both calls, before the plugin is split in two.
    [Fact]
    public void InitializeUi_AnEntryTypeThatIsBothParts_GetsInitializeAndInitializeUi()
    {
        var discovered = _Discovered("both", uiAssembly: null, uiEntryType: null);
        var manager = _Manager(new PluginDiagnostics());
        var plugin = new BothParts();
        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => plugin);
        var uiHost = Substitute.For<ICockpitUiHost>();

        manager.Initialize((_, _) => Substitute.For<ICockpitHost>());
        _UiManager(manager, new PluginDiagnostics()).InitializeUi(PluginUiManager.ActivateUi, (_, _) => uiHost);

        Assert.Equal(1, plugin.InitializeCount);
        Assert.Same(uiHost, plugin.UiHost);
    }

    [Fact]
    public void LoadAndConfigure_APluginThatIsOnlyAUiPart_IsNeverActivatedAsABackend_ButGetsInitializeUi()
    {
        var manifest = new PluginManifest("clock", "Clock", "1.0", EntryAssembly: null, 2, null, null, null, null, UiAssembly: "Clock.dll");
        var discovered = new DiscoveredPlugin("/plugins/clock", "clock", manifest, "hash", PluginLoadDecision.Load);
        var manager = _Manager(new PluginDiagnostics());
        var backendActivations = 0;
        var ui = new RecordingUiPart();

        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => new BackendPart($"activation {++backendActivations}"));
        _UiManager(manager, new PluginDiagnostics()).InitializeUi((_, _) => ui, (_, _) => Substitute.For<ICockpitUiHost>());

        Assert.Equal(0, backendActivations);
        Assert.NotNull(ui.Host);
        Assert.Same(discovered, Assert.Single(manager.Loaded));
    }

    // Each row is one member of the UI host that reaches the desktop through the plugin's own host or hub.
    [Theory]
    [MemberData(nameof(UiHostForwards))]
    public async Task CockpitUiHost_ForwardsToTheSamePluginsCockpit(
        string member, Func<ICockpitUiHost, IReadOnlyList<string>, Task<object?>> read, object expected)
    {
        var channels = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        channels.For("diagram").Handle("echo", (payload, _) => Task.FromResult(payload));
        var host = Substitute.For<ICockpitHost>();
        host.Sessions.ActivePaneId.Returns("pane-7");
        var views = new ViewRequests();
        var services = new ServiceCollection()
            .AddSingleton(channels)
            .AddSingleton<IEmbeddedSessionHost>(views)
            .BuildServiceProvider();

        var result = await read(new CockpitUiHost("diagram", host, services), views.PaneIds);

        Assert.Equal((member, expected), (member, result));
    }

    public static TheoryData<string, Func<ICockpitUiHost, IReadOnlyList<string>, Task<object?>>, object> UiHostForwards => new()
    {
        { "ActivePaneId", (host, _) => Task.FromResult<object?>(host.ActivePaneId), "pane-7" },
        {
            "CreateEmbeddedSessionView",
            (host, requested) =>
            {
                host.CreateEmbeddedSessionView("pane-3");
                return Task.FromResult<object?>(requested.Single());
            },
            "pane-3"
        },
        {
            "Channel.InvokeAsync",
            async (host, _) => (await host.Channel.InvokeAsync("echo", JsonSerializer.SerializeToElement("ping"))).GetString(),
            "ping"
        },
    };

    private static PluginManager _Manager(PluginDiagnostics diagnostics) => new(NullLogger<PluginManager>.Instance, diagnostics);

    private static PluginUiManager _UiManager(PluginManager backend, PluginDiagnostics diagnostics) =>
        new(NullLogger<PluginUiManager>.Instance, diagnostics, backend);

    private static DiscoveredPlugin _Discovered(string id, string? uiAssembly, string? uiEntryType) => new(
        AppContext.BaseDirectory, id,
        new PluginManifest(id, id, "1.0", $"{id}.dll", 2, null, null, null, null, UiAssembly: uiAssembly, UiEntryType: uiEntryType),
        "hash", PluginLoadDecision.Load);

    private class BackendPart(string id) : ICockpitPlugin
    {
        public PluginMetadata Metadata { get; } = new(id, id, "1.0", null, null);

        public int InitializeCount { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Initialize(ICockpitHost host) => InitializeCount++;

        public void Dispose()
        {
        }
    }

    private sealed class BothParts() : BackendPart("both"), ICockpitPluginUi
    {
        public ICockpitUiHost? UiHost { get; private set; }

        public void InitializeUi(ICockpitUiHost host) => UiHost = host;
    }

    // Activated by type name through the real activator, so what it received is read back through LastHost.
    public sealed class RecordingUiPart : ICockpitPluginUi
    {
        public static readonly AsyncLocal<ICockpitUiHost?> LastHost = new();

        public ICockpitUiHost? Host { get; private set; }

        public void InitializeUi(ICockpitUiHost host)
        {
            Host = host;
            LastHost.Value = host;
        }
    }

    // Records which pane a view was asked for and has none: building a real Control here, off the UI thread,
    // is what breaks neighbouring view tests.
    private sealed class ViewRequests : IEmbeddedSessionHost
    {
        public List<string> PaneIds { get; } = [];

        public IEmbeddedSession Embed(string workspaceId, EmbeddedSessionRequest request) => throw new NotSupportedException();

        public void CloseForWorkspace(string workspaceId)
        {
        }

        public Control? EmbeddedSessionView(string paneId)
        {
            PaneIds.Add(paneId);
            return null;
        }
    }
}
