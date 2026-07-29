using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>
/// <see cref="KimiMcpConfig"/> (AC-269 sub [b], D6) — the epic's costliest trap: a stdio server's wire object
/// must carry no <c>type</c> property at all, or kimi's adapter silently drops it. Serializes the built wire
/// objects exactly as <see cref="KimiAcpConnection"/> would (its own <c>_jsonOptions</c> only ignores explicit
/// nulls, so a present-but-null <c>type</c> would still slip through — the shape itself must omit the property).
/// </summary>
public class KimiMcpConfigTests
{
    private static readonly Dictionary<string, string?> _NoEnv = new();

    [Fact]
    public void Build_WithNoServers_IsEmpty()
    {
        Assert.Empty(KimiMcpConfig.Build(null, _NoEnv));
        Assert.Empty(KimiMcpConfig.Build([], _NoEnv));
    }

    // D6, the regression test the brief singles out: a stdio server's serialized JSON must have no "type"
    // property whatsoever — "type":"stdio" would make kimi's adapter drop the server silently.
    [Fact]
    public void Build_ForAStdioServer_SerializesWithNoTypeProperty()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "fs", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem"] }], _NoEnv);

        var json = _SerializeSingle(wire);
        Assert.False(json.TryGetProperty("type", out _));
        Assert.Equal("fs", json.GetProperty("name").GetString());
        Assert.Equal("npx", json.GetProperty("command").GetString());
        Assert.Equal(new[] { "-y", "@modelcontextprotocol/server-filesystem" }, json.GetProperty("args").EnumerateArray().Select(element => element.GetString()));
    }

    [Fact]
    public void Build_ForAnHttpServer_SerializesWithTypeHttp()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "cockpit-orchestrator", Url = "http://127.0.0.1:8765/mcp" }], _NoEnv);

        var json = _SerializeSingle(wire);
        Assert.Equal("http", json.GetProperty("type").GetString());
        Assert.Equal("cockpit-orchestrator", json.GetProperty("name").GetString());
        Assert.Equal("http://127.0.0.1:8765/mcp", json.GetProperty("url").GetString());
        Assert.Equal(0, json.GetProperty("headers").GetArrayLength());
    }

    [Fact]
    public void Build_ForAnHttpServerWithABearerToken_PutsItLiterallyInTheHeadersArray()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = "yt-pat-value" }], _NoEnv);

        var headers = _SerializeSingle(wire).GetProperty("headers");
        Assert.Equal(1, headers.GetArrayLength());
        Assert.Equal("Authorization", headers[0].GetProperty("name").GetString());
        Assert.Equal("Bearer yt-pat-value", headers[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Build_ForACockpitHostedServer_ReadsTheBearerFromTheResolvedEnvironment_NotFromBearerToken()
    {
        var environment = new Dictionary<string, string?> { [WellKnownSessionEnvironment.CockpitMcpKey] = "run-key" };
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }], environment);

        var headers = _SerializeSingle(wire).GetProperty("headers");
        Assert.Equal("Bearer run-key", headers[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Build_SkipsAServerWithNeitherUrlNorCommand()
    {
        Assert.Empty(KimiMcpConfig.Build([new PluginMcpServer { Name = "broken" }], _NoEnv));
    }

    [Fact]
    public void Build_MixesStdioAndHttpServers_EachWithItsOwnShape()
    {
        var wire = KimiMcpConfig.Build(
        [
            new PluginMcpServer { Name = "fs", Command = "npx" },
            new PluginMcpServer { Name = "api", Url = "http://x/mcp" },
        ], _NoEnv);

        Assert.Equal(2, System.Linq.Enumerable.Count(wire));
        var json = JsonSerializer.Serialize(wire);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement[0].TryGetProperty("type", out _));
        Assert.Equal("http", document.RootElement[1].GetProperty("type").GetString());
    }

    private static JsonElement _SerializeSingle(IReadOnlyList<object> wire)
    {
        Assert.Single(wire);
        var json = JsonSerializer.Serialize(wire[0]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
