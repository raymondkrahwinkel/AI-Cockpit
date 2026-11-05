using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Proxmox.Settings;

internal sealed class ProxmoxSettings(IPluginStorage storage)
{
    private const string ServersKey = "servers";
    private const string TokensKey = "apiTokens";

    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    public string Host => _Current().Host;
    public int Port => _Current().Port;
    public string TokenId => _Current().TokenId;
    public string ApiToken => GetApiToken(_Current().Id);
    public string TrustedCertFingerprint => _Current().TrustedCertFingerprint;
    public bool AllowRollback => _Current().AllowRollback;
    public bool AllowDelete => _Current().AllowDelete;
    public bool IsConfigured => Servers.Count == 1 && _Current().IsConfigured && !string.IsNullOrWhiteSpace(ApiToken);

    public IReadOnlyList<ProxmoxServer> Servers
    {
        get
        {
            _MigrateLegacySettings();
            return storage.Get<List<ProxmoxServer>>(ServersKey) ?? [];
        }
    }

    public string GetApiToken(string serverId)
    {
        _MigrateLegacySettings();
        var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(storage.GetSecret(TokensKey) ?? "{}") ?? [];
        return tokens.GetValueOrDefault(serverId) ?? string.Empty;
    }

    public void SaveServers(IEnumerable<ProxmoxServer> servers, IReadOnlyDictionary<string, string> tokens)
    {
        var values = servers.ToList();
        storage.Set(ServersKey, values);
        storage.SetSecret(TokensKey, JsonSerializer.Serialize(tokens.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToDictionary()));
        storage.SetSecret("apiToken", string.Empty);
    }

    public ProxmoxServer ResolveServer(string? name)
    {
        var servers = Servers;
        if (servers.Count == 0)
        {
            throw new ProxmoxApiException("No Proxmox server is configured yet. Add one in the plugin settings.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return servers.Count == 1
                ? servers[0]
                : throw new ProxmoxApiException($"Select a Proxmox server. Available servers: {string.Join(", ", servers.Select(server => server.Name))}.");
        }

        return servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProxmoxApiException($"Proxmox server \"{name}\" is not configured. Available servers: {string.Join(", ", servers.Select(server => server.Name))}.");
    }

    private ProxmoxServer _Current() => ResolveServer(null);

    private void _MigrateLegacySettings()
    {
        if (storage.Get<List<ProxmoxServer>>(ServersKey) is not null)
        {
            return;
        }

        var host = storage.Get<string>("host") ?? string.Empty;
        var tokenId = storage.Get<string>("tokenId") ?? string.Empty;
        var token = storage.GetSecret("apiToken") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(tokenId) && string.IsNullOrWhiteSpace(token))
        {
            storage.Set(ServersKey, new List<ProxmoxServer>());
            return;
        }

        var server = new ProxmoxServer(Guid.NewGuid().ToString("N"), host, host, storage.Get<int?>("port") ?? 8006, tokenId,
            storage.Get<string>("trustedCertFingerprint") ?? string.Empty, storage.Get<bool?>("allowRollback") ?? false, storage.Get<bool?>("allowDelete") ?? false);
        SaveServers([server], new Dictionary<string, string> { [server.Id] = token });
    }
}

internal enum ProxmoxServerKind
{
    Pve,
}

internal sealed record ProxmoxServer(string Id, string Name, string Host, int Port, string TokenId, string TrustedCertFingerprint, bool AllowRollback, bool AllowDelete, ProxmoxServerKind Kind = ProxmoxServerKind.Pve)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(TokenId);
}
