using System.Text.Json.Serialization;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `NodeEndpointSettings` in the `nodeEndpoint` section of `cockpit.json` (AC-790) — the
// network-node master switch and its persistent shared secret, off unless the operator turned it on.
//
// `SharedSecret` deliberately keeps that exact name: `SecretFields` (Cockpit.Core.Secrets) recognises any field
// whose name contains "secret" as a credential and encrypts/scrubs it automatically — no separate registration.
//
// AC-792 adds `Pairing`: who that secret was granted to. Absent for a node that was never paired — including
// every config written before this existed, which reads back as an unpaired node with its hand-copied secret
// intact, so nothing has to migrate.
internal sealed class NodeEndpointSettingsEntry
{
    public bool Enabled { get; set; }
    public string? SharedSecret { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodePairingEntry? Pairing { get; set; }

    // AC-793: CIDR ranges allowed to see this node from outside its own local network. Null (not an empty array)
    // for every config written before this existed, which reads back through `ToDomain` as the same empty list
    // `NodeEndpointSettings.AllowedDiscoveryRanges` defaults to — nothing to migrate.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedDiscoveryRanges { get; set; }

    public static NodeEndpointSettingsEntry FromDomain(NodeEndpointSettings settings) => new()
    {
        Enabled = settings.Enabled,
        SharedSecret = settings.SharedSecret,
        Pairing = settings.Pairing is null ? null : NodePairingEntry.FromDomain(settings.Pairing),
        AllowedDiscoveryRanges = settings.AllowedDiscoveryRanges.Count == 0 ? null : [.. settings.AllowedDiscoveryRanges],
    };

    public NodeEndpointSettings ToDomain() => new()
    {
        Enabled = Enabled,
        SharedSecret = SharedSecret ?? "",
        Pairing = Pairing?.ToDomain(),
        AllowedDiscoveryRanges = AllowedDiscoveryRanges ?? [],
    };
}

// On-disk shape of `NodePairing`. Nothing here is a credential — the secret it belongs to is the sibling field —
// so this stays readable in the config the operator opens to see what their cockpit is attached to.
internal sealed class NodePairingEntry
{
    public string ControllerName { get; set; } = string.Empty;
    public string ControllerAddress { get; set; } = string.Empty;
    public DateTimeOffset PairedAtUtc { get; set; }

    // AC-794: which profiles and projects this pairing may use. Null (not an empty array) for a pairing written
    // before this existed, which reads back through `ToDomain` as the same empty list `NodePairing`'s own
    // properties default to — nothing to migrate, and a pre-AC-794 pairing starts able to use nothing, the same
    // posture a fresh one takes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedProfileLabels { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedProjectIds { get; set; }

    public static NodePairingEntry FromDomain(NodePairing pairing) => new()
    {
        ControllerName = pairing.ControllerName,
        ControllerAddress = pairing.ControllerAddress,
        PairedAtUtc = pairing.PairedAtUtc,
        AllowedProfileLabels = pairing.AllowedProfileLabels.Count == 0 ? null : [.. pairing.AllowedProfileLabels],
        AllowedProjectIds = pairing.AllowedProjectIds.Count == 0 ? null : [.. pairing.AllowedProjectIds],
    };

    public NodePairing ToDomain() => new()
    {
        ControllerName = ControllerName,
        ControllerAddress = ControllerAddress,
        PairedAtUtc = PairedAtUtc,
        AllowedProfileLabels = AllowedProfileLabels ?? [],
        AllowedProjectIds = AllowedProjectIds ?? [],
    };
}
