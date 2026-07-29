using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeMcpConfig"/> (#26): the shared MCP registry written into Claude's <c>--mcp-config</c>. The
/// load-bearing property (AC-40) is that a cockpit-hosted endpoint's auth is written as an <em>env-var reference</em>
/// (<c>${COCKPIT_MCP_KEY}</c>), never a literal key, so nothing sensitive lands in the file — while a user API-key
/// server still gets its own literal bearer.
/// </summary>
public class ClaudeMcpConfigTests
{
    [Fact]
    public void Write_ForACockpitHostedServer_ReferencesTheAuthKeyEnvVar_NotALiteral()
    {
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:1/mcp", CockpitHosted = true }]);

        try
        {
            Assert.Equal("Bearer ${COCKPIT_MCP_KEY}", _Authorization(path!, "cockpit-session"));
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Fact]
    public void Write_ForAUserApiKeyServer_WritesItsOwnBearerLiteral()
    {
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp", BearerToken = "yt-key" }]);

        try
        {
            Assert.Equal("Bearer yt-key", _Authorization(path!, "youtrack"));
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Fact]
    public void Write_ForAUserApiKeyServer_WritesTheTokenFileOwnerOnly()
    {
        // The file carries a literal third-party bearer token; it must not sit world-readable in a shared temp
        // directory the way it used to (AC-63).
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp", BearerToken = "yt-key" }]);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No Unix mode bits on Windows — the protection is the per-user temp profile. Assert only that the
                // token did land in the file, so the test still exercises the write on this platform.
                Assert.Contains("yt-key", File.ReadAllText(path!));
                return;
            }

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path!));
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Fact]
    public void Write_WithNoServers_ReturnsNull_ByDefault()
    {
        // The TTY route's existing behaviour (unchanged by AC-378): nothing to add means the flag is dropped
        // entirely, so the operator's own connectors add on top of a config that was never written at all.
        Assert.Null(ClaudeMcpConfig.Write([]));
    }

    [Fact]
    public void Write_WithNoServers_WriteEmptyExplicit_WritesAnActualEmptyConfig_InsteadOfNull()
    {
        // AC-378, criterion 4 — the empty-resolution trap: the headless/strict route must be able to say "zero
        // servers" as an explicit, on-disk {"mcpServers":{}}, not as a dropped flag that lets the CLI fall back to
        // its own user/project config.
        var path = ClaudeMcpConfig.Write([], writeEmptyExplicit: true);

        try
        {
            Assert.NotNull(path);
            Assert.Empty(JsonNode.Parse(File.ReadAllText(path!))!["mcpServers"]!.AsObject());
        }
        finally
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Write_WriteEmptyExplicit_StillWritesTheServersItWasGiven()
    {
        // writeEmptyExplicit only changes what happens at zero servers — it must not suppress the servers that
        // were actually resolved.
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp" }], writeEmptyExplicit: true);

        try
        {
            Assert.Equal("http://example/mcp", JsonNode.Parse(File.ReadAllText(path!))!["mcpServers"]!["youtrack"]!["url"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(path!);
        }
    }

    private static string _Authorization(string path, string serverName) =>
        JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]![serverName]!["headers"]!["Authorization"]!.GetValue<string>();
}
