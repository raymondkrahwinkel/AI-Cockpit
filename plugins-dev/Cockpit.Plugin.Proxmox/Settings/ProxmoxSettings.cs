using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Proxmox.Settings;

// The plugin's settings (AC-1038): one Proxmox target, like Docker's single daemon rather than Kubernetes' cluster
// list. Read fresh from `IPluginStorage` on every access, so a settings save takes effect without a restart.
internal sealed class ProxmoxSettings(IPluginStorage storage)
{
    // Whether the cockpit-proxmox MCP server is offered to sessions. On by default.
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    // The Proxmox API host, e.g. "pve.example.lan". Blank means no target is configured yet.
    public string Host
    {
        get => storage.Get<string>("host") ?? string.Empty;
        set => storage.Set("host", value);
    }

    // The Proxmox API port. Defaults to Proxmox's standard 8006.
    public int Port
    {
        get => storage.Get<int?>("port") ?? 8006;
        set => storage.Set("port", value);
    }

    // The token's identity, `user@realm!tokenid` — not secret by itself; the token's UUID is (see `ApiToken`).
    public string TokenId
    {
        get => storage.Get<string>("tokenId") ?? string.Empty;
        set => storage.Set("tokenId", value);
    }

    // The token's UUID. Written through the secret layer, so it is encrypted at rest when the operator has that on.
    public string ApiToken
    {
        get => storage.GetSecret("apiToken") ?? string.Empty;
        set => storage.SetSecret("apiToken", value);
    }

    // The SHA-256 fingerprint (hex, lower-case) of the certificate the operator explicitly confirmed trusting, or
    // empty when none has been trusted yet. Not secret — a fingerprint identifies, it does not authenticate.
    public string TrustedCertFingerprint
    {
        get => storage.Get<string>("trustedCertFingerprint") ?? string.Empty;
        set => storage.Set("trustedCertFingerprint", value);
    }

    // Whether rolling back a VM/LXC snapshot is offered. Off by default — destructive for everything since the snapshot.
    public bool AllowRollback
    {
        get => storage.Get<bool?>("allowRollback") ?? false;
        set => storage.Set("allowRollback", value);
    }

    // Whether deleting a VM or LXC container is offered. Off by default.
    public bool AllowDelete
    {
        get => storage.Get<bool?>("allowDelete") ?? false;
        set => storage.Set("allowDelete", value);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(TokenId) && !string.IsNullOrWhiteSpace(ApiToken);
}
