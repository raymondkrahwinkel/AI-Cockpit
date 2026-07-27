using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>Custom headers (AC-354) on the Kimi route, whose wire form is an array of name/value pairs.</summary>
public class KimiCustomHeaderTests
{
    private static readonly Dictionary<string, string?> NoEnvironment = [];

    private static JsonElement _SerializeSingle(object wire) =>
        JsonDocument.Parse(JsonSerializer.Serialize(wire)).RootElement.EnumerateArray().Single();

    private static Dictionary<string, string> _HeadersOf(JsonElement server) =>
        server.GetProperty("headers")
            .EnumerateArray()
            .ToDictionary(
                header => header.GetProperty("name").GetString() ?? string.Empty,
                header => header.GetProperty("value").GetString() ?? string.Empty);

    [Fact]
    public void Build_CarriesACustomHeader()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer
        {
            Name = "private-api",
            Url = "https://api.example/mcp",
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "the-key" },
        }], NoEnvironment);

        Assert.Equal("the-key", _HeadersOf(_SerializeSingle(wire))["X-Api-Key"]);
    }

    [Fact]
    public void Build_CarriesACustomHeaderBesideTheBearer()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer
        {
            Name = "private-api",
            Url = "https://api.example/mcp",
            BearerToken = "the-token",
            Headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" },
        }], NoEnvironment);

        var headers = _HeadersOf(_SerializeSingle(wire));
        Assert.Equal("acme", headers["X-Tenant"]);
        Assert.Equal("Bearer the-token", headers["Authorization"]);
    }

    [Fact]
    public void Build_WithNothingToSend_WritesAnEmptyHeaderArray()
    {
        var wire = KimiMcpConfig.Build(
            [new PluginMcpServer { Name = "plain", Url = "https://api.example/mcp" }],
            NoEnvironment);

        Assert.Empty(_HeadersOf(_SerializeSingle(wire)));
    }
}
