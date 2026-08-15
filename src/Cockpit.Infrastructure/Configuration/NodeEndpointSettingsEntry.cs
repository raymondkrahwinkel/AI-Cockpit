using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `NodeEndpointSettings` in the `nodeEndpoint` section of `cockpit.json` (AC-790) — the
// network-node master switch and its persistent shared secret, off unless the operator turned it on.
//
// `SharedSecret` deliberately keeps that exact name: `SecretFields` (Cockpit.Core.Secrets) recognises any field
// whose name contains "secret" as a credential and encrypts/scrubs it automatically — no separate registration.
internal sealed class NodeEndpointSettingsEntry
{
    public bool Enabled { get; set; }
    public string? SharedSecret { get; set; }

    public static NodeEndpointSettingsEntry FromDomain(NodeEndpointSettings settings) => new()
    {
        Enabled = settings.Enabled,
        SharedSecret = settings.SharedSecret,
    };

    public NodeEndpointSettings ToDomain() => new() { Enabled = Enabled, SharedSecret = SharedSecret ?? "" };
}
