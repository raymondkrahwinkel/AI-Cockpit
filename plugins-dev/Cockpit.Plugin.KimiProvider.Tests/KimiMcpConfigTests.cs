using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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
        KimiMcpConfig.Build(null, _NoEnv).Should().BeEmpty();
        KimiMcpConfig.Build([], _NoEnv).Should().BeEmpty();
    }

    // D6, the regression test the brief singles out: a stdio server's serialized JSON must have no "type"
    // property whatsoever — "type":"stdio" would make kimi's adapter drop the server silently.
    [Fact]
    public void Build_ForAStdioServer_SerializesWithNoTypeProperty()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "fs", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem"] }], _NoEnv);

        var json = _SerializeSingle(wire);
        json.TryGetProperty("type", out _).Should().BeFalse("a stdio server must be recognised by the absence of \"type\", not by \"type\":\"stdio\"");
        json.GetProperty("name").GetString().Should().Be("fs");
        json.GetProperty("command").GetString().Should().Be("npx");
        json.GetProperty("args").EnumerateArray().Select(element => element.GetString()).Should().Equal("-y", "@modelcontextprotocol/server-filesystem");
    }

    [Fact]
    public void Build_ForAnHttpServer_SerializesWithTypeHttp()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "cockpit-orchestrator", Url = "http://127.0.0.1:8765/mcp" }], _NoEnv);

        var json = _SerializeSingle(wire);
        json.GetProperty("type").GetString().Should().Be("http");
        json.GetProperty("name").GetString().Should().Be("cockpit-orchestrator");
        json.GetProperty("url").GetString().Should().Be("http://127.0.0.1:8765/mcp");
        json.GetProperty("headers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Build_ForAnHttpServerWithABearerToken_PutsItLiterallyInTheHeadersArray()
    {
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = "yt-pat-value" }], _NoEnv);

        var headers = _SerializeSingle(wire).GetProperty("headers");
        headers.GetArrayLength().Should().Be(1);
        headers[0].GetProperty("name").GetString().Should().Be("Authorization");
        headers[0].GetProperty("value").GetString().Should().Be("Bearer yt-pat-value");
    }

    [Fact]
    public void Build_ForACockpitHostedServer_ReadsTheBearerFromTheResolvedEnvironment_NotFromBearerToken()
    {
        var environment = new Dictionary<string, string?> { [WellKnownSessionEnvironment.CockpitMcpKey] = "run-key" };
        var wire = KimiMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }], environment);

        var headers = _SerializeSingle(wire).GetProperty("headers");
        headers[0].GetProperty("value").GetString().Should().Be("Bearer run-key");
    }

    [Fact]
    public void Build_SkipsAServerWithNeitherUrlNorCommand()
    {
        KimiMcpConfig.Build([new PluginMcpServer { Name = "broken" }], _NoEnv).Should().BeEmpty();
    }

    [Fact]
    public void Build_MixesStdioAndHttpServers_EachWithItsOwnShape()
    {
        var wire = KimiMcpConfig.Build(
        [
            new PluginMcpServer { Name = "fs", Command = "npx" },
            new PluginMcpServer { Name = "api", Url = "http://x/mcp" },
        ], _NoEnv);

        wire.Should().HaveCount(2);
        var json = JsonSerializer.Serialize(wire);
        using var document = JsonDocument.Parse(json);
        document.RootElement[0].TryGetProperty("type", out _).Should().BeFalse();
        document.RootElement[1].GetProperty("type").GetString().Should().Be("http");
    }

    private static JsonElement _SerializeSingle(IReadOnlyList<object> wire)
    {
        wire.Should().ContainSingle();
        var json = JsonSerializer.Serialize(wire[0]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
