using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Proxmox.UI;

internal sealed class ProxmoxSettings(IPluginStorage storage)
{
    private const string ServersKey = "servers";
    private const string TokensKey = "apiTokens";

    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    public string Host { get => _Current().Host; set => _SaveCurrent(server => server with { Host = value }); }
    public int Port { get => _Current().Port; set => _SaveCurrent(server => server with { Port = value }); }
    public string TokenId { get => _Current().TokenId; set => _SaveCurrent(server => server with { TokenId = value }); }
    public string ApiToken { get => GetApiToken(_Current().Id); set => _SaveCurrent(server => server, value); }
    public string TrustedCertFingerprint { get => _Current().TrustedCertFingerprint; set => _SaveCurrent(server => server with { TrustedCertFingerprint = value }); }
    public bool AllowRollback { get => _Current().AllowRollback; set => _SaveCurrent(server => server with { AllowRollback = value }); }
    public bool AllowDelete { get => _Current().AllowDelete; set => _SaveCurrent(server => server with { AllowDelete = value }); }
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
        storage.Set(ServersKey, servers.ToList());
        storage.SetSecret(TokensKey, JsonSerializer.Serialize(tokens.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToDictionary()));
        storage.SetSecret("apiToken", string.Empty);
    }

    private ProxmoxServer _Current()
    {
        var servers = Servers;
        return servers.Count > 0 ? servers[0] : new ProxmoxServer(Guid.NewGuid().ToString("N"), string.Empty, string.Empty, 8006, string.Empty, string.Empty, false, false);
    }

    private void _SaveCurrent(Func<ProxmoxServer, ProxmoxServer> change, string? token = null)
    {
        var server = change(_Current());
        var tokens = Servers.ToDictionary(value => value.Id, value => GetApiToken(value.Id));
        if (token is not null)
        {
            tokens[server.Id] = token;
        }

        var servers = Servers.ToList();
        if (servers.Count == 0)
        {
            servers.Add(server);
        }
        else
        {
            servers[0] = server;
        }

        SaveServers(servers, tokens);
    }

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

internal sealed record ProxmoxServer(string Id, string Name, string Host, int Port, string TokenId, string TrustedCertFingerprint, bool AllowRollback, bool AllowDelete)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(TokenId);
}
