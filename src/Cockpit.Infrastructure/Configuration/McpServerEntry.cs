using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of an <see cref="McpServerConfig"/> in the <c>mcpServers</c> section of <c>cockpit.json</c>.</summary>
internal sealed class McpServerEntry
{
    public string Name { get; set; } = string.Empty;

    public McpTransport Transport { get; set; }

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public string? Url { get; set; }

    public McpServerAuth Auth { get; set; }

    public string? ApiKey { get; set; }

    public string? OAuthAuthority { get; set; }

    public string? OAuthClientId { get; set; }

    public bool Enabled { get; set; } = true;

    public static McpServerEntry FromDomain(McpServerConfig server) => new()
    {
        Name = server.Name,
        Transport = server.Transport,
        Command = server.Command,
        Args = [.. server.Args],
        Url = server.Url,
        Auth = server.Auth,
        ApiKey = server.ApiKey,
        OAuthAuthority = server.OAuthAuthority,
        OAuthClientId = server.OAuthClientId,
        Enabled = server.Enabled,
    };

    public McpServerConfig ToDomain() => new()
    {
        Name = Name,
        Transport = Transport,
        Command = Command,
        Args = Args,
        Url = Url,
        Auth = Auth,
        ApiKey = ApiKey,
        OAuthAuthority = OAuthAuthority,
        OAuthClientId = OAuthClientId,
        Enabled = Enabled,
    };
}
