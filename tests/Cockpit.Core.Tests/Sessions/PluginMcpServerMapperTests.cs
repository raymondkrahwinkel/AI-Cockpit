using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Sessions;

public class PluginMcpServerMapperTests
{
    [Fact]
    public void ToPluginMcpServer_MapsEverySupportedTransportAndRejectsIncompleteConfigurations()
    {
        var proxy = PluginMcpServerMapper.ToPluginMcpServer(
            new McpServerConfig { Name = "proxy", Transport = McpTransport.Http }, "token", "http://127.0.0.1:9000/mcp");
        var credential = PluginMcpServerMapper.ToPluginMcpServer(
            new McpServerConfig { Name = "credential", Transport = McpTransport.Http, Url = "https://example.test/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "token", Headers = [new("X-Key", "value")] }, "oauth", null);
        var hosted = PluginMcpServerMapper.ToPluginMcpServer(
            new McpServerConfig { Name = "hosted", Transport = McpTransport.Http, Url = "http://127.0.0.1:8080/mcp", CockpitHosted = true }, null, null);
        var stdio = PluginMcpServerMapper.ToPluginMcpServer(
            new McpServerConfig { Name = "stdio", Transport = McpTransport.Stdio, Command = "npx", Args = ["-y", "server"] }, null, null);

        Assert.Equal(("proxy", "http://127.0.0.1:9000/mcp", null, null, true), (proxy!.Name, proxy.Url, proxy.Command, proxy.BearerToken, proxy.CockpitHosted));
        Assert.Equal(new Dictionary<string, string> { ["X-Key"] = "value" }, credential!.Headers);
        Assert.Equal(("credential", "https://example.test/mcp", null, "token", false), (credential.Name, credential.Url, credential.Command, credential.BearerToken, credential.CockpitHosted));
        Assert.Equal(("hosted", "http://127.0.0.1:8080/mcp", null, null, true), (hosted!.Name, hosted.Url, hosted.Command, hosted.BearerToken, hosted.CockpitHosted));
        Assert.Equal(("stdio", null, "npx", null, false), (stdio!.Name, stdio.Url, stdio.Command, stdio.BearerToken, stdio.CockpitHosted));
        Assert.Equal(["-y", "server"], stdio.Args);
        Assert.Null(PluginMcpServerMapper.ToPluginMcpServer(new McpServerConfig { Name = "missing-http", Transport = McpTransport.Http }, null, null));
        Assert.Null(PluginMcpServerMapper.ToPluginMcpServer(new McpServerConfig { Name = "missing-stdio", Transport = McpTransport.Stdio }, null, null));
    }
}
