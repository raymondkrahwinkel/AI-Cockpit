namespace Cockpit.Core.Swarm;

// AC-1916: the swarm's own settings, under the `Swarm` section of `cockpit.json`.
public sealed record SwarmSettings
{
    public SwarmLowerRankReturn WhenALowerRankComesBack { get; init; } = SwarmLowerRankReturn.AnnouncedTakeover;

    // CIDR ranges the operator counts as internal on top of `InternalNetworkRanges.BuiltIn`, e.g. a VPN's
    // "100.64.0.0/10". Empty by default: a VPN's range counts as internal only once it is listed here.
    public IReadOnlyList<string> ExtraInternalCidrs { get; init; } = [];

    public IReadOnlyDictionary<string, string> PairingOrigins { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<SwarmManualMember> ManualAddresses { get; init; } = [];

    public static SwarmSettings Default { get; } = new();
}

public enum SwarmLowerRankReturn { AnnouncedTakeover, JoinAtBack }

public sealed record SwarmManualMember(string Address, string? MemberId = null, string Status = "Not reachable",
    string ErrorCode = "", string ErrorMessage = "");
