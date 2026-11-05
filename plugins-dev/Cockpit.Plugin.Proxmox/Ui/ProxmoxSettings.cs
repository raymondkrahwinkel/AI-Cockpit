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
