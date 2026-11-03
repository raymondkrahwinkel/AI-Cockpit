using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

public class KimiMcpConfigTests
{
    [Fact]
    public void Build_ForACockpitHostedServer_ReadsTheBearerFromTheResolvedEnvironment_NotFromBearerToken()
    {
        var environment = new Dictionary<string, string?> { [WellKnownSessionEnvironment.CockpitMcpKey] = "run-key" };
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }], environment);

        var headers = _SerializeSingle(wire).GetProperty("headers");
        Assert.Equal("Bearer run-key", headers[0].GetProperty("value").GetString());
    }

    private static JsonElement _SerializeSingle(IReadOnlyList<object> wire)
    {
        Assert.Single(wire);
        var json = JsonSerializer.Serialize(wire[0]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
