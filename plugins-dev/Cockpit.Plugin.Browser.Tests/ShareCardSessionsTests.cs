extern alias browserBackend;

using System.Reflection;
using System.Text.Json;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static browserBackend::Cockpit.Plugin.Browser.TabShareContract;

namespace Cockpit.Plugin.Browser.Tests;

public sealed class ShareCardSessionsTests
{
    // L2: the backend/UI channel offers only sessions whose mount permits browser calls.
    [Fact]
    public async Task Sessions_OnlyOffersPanesWithTheBrowserServerMounted()
    {
        var mounts = new SessionMcpMounts();
        mounts.Grant("with-browser", ["cockpit-browser"]);
        mounts.Grant("without-browser", ["cockpit-session"]);
        using var services = new ServiceCollection().AddSingleton(mounts).BuildServiceProvider();
        var observer = new PluginBackendSessionObserver(new SessionRegistry(), services);
        var host = DispatchProxy.Create<ICockpitHost, ShareHost>();
        var fake = (ShareHost)host;
        fake.Observer = new ShareObserver(observer);
        using var bridge = new TabShareBridge(host);

        JsonElement answer = await fake.Channel.Handlers[Sessions](JsonSerializer.SerializeToElement(true), CancellationToken.None);

        OpenCockpitSession[]? sessions = answer.Deserialize<OpenCockpitSession[]>(Json);
        Assert.NotNull(sessions);
        Assert.Equal([new OpenCockpitSession("with-browser", "With browser")], sessions);
    }

    private sealed class ShareObserver(ICockpitSessionObserver mounts) : ICockpitSessionObserver
    {
        public IReadOnlyList<OpenCockpitSession> OpenSessions =>
            [new("without-browser", "Without browser"), new("with-browser", "With browser"), new("unknown", "Unknown")];
        public bool HasMcpServer(string paneId, string serverName) => mounts.HasMcpServer(paneId, serverName);
        public event EventHandler<SessionOutputText>? OutputProduced { add { } remove { } }
    }

    public class ShareHost : DispatchProxy
    {
        public ICockpitSessionObserver Observer { get; set; } = NullCockpitSessionObserver.Instance;
        public ShareChannel Channel { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Channel" => Channel,
            "get_Sessions" => Observer,
            _ => throw new InvalidOperationException(targetMethod?.Name),
        };
    }

    public sealed class ShareChannel : IPluginBackendChannel, IDisposable
    {
        public Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> Handlers { get; } = [];
        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
        {
            Handlers.Add(action, handler);
            return this;
        }
        public void Publish(string name, JsonElement payload) => throw new InvalidOperationException(name);
        public void Dispose() { }
    }
}
