namespace Cockpit.Plugin.Depot.Contracts;

// AC-2224: pairing a phone from a connection's row, and the phones paired to it.
internal static class DepotMobileChannel
{
    // Payload: DepotMobileRequest. Asks Depot for a pairing id and answers a DepotPairingStatus with the QR code.
    public const string PairStart = "mobile-pair-start";

    // Payload: DepotMobileRequest. The open pairing's state, without the QR code. Answers a DepotPairingStatus.
    public const string PairStatus = "mobile-pair-status";

    // Payload: DepotMobileRequest. Discards the open pairing and wipes its secret.
    public const string PairCancel = "mobile-pair-cancel";

    // Payload: DepotMobileRequest. Answers the DepotPairedPhone list.
    public const string Phones = "mobile-phones";

    // Payload: DepotMobileRequest with DeviceId. Wipes that phone's key here; Depot keeps its grant.
    public const string Forget = "mobile-phone-forget";
}

// `Refresh` asks Depot for the phones' current names; only an explicit unpair removes a saved phone.
internal sealed record DepotMobileRequest(string ConnectionId, string? DeviceId = null, bool Refresh = false);

// `State` is a DepotPairingState value. `Qr` is the QR code's text, only in PairStart's answer. `Link` is Depot's
// Account › Mobile app page, for a reason the operator can do something about there.
internal sealed record DepotPairingStatus(
    string State,
    string? Message = null,
    string? Qr = null,
    DateTimeOffset? ExpiresAt = null,
    string? DeviceName = null,
    string? Link = null);

internal static class DepotPairingState
{
    public const string Open = "open";

    public const string Waiting = "waiting";

    public const string Paired = "paired";

    public const string Expired = "expired";

    public const string Unavailable = "unavailable";

    public const string Failed = "failed";

    public const string None = "none";
}

internal sealed record DepotPairedPhone(string DeviceId, string Name, DateTimeOffset PairedAt);

internal sealed record DepotPairedPhones(IReadOnlyList<DepotPairedPhone> Phones, string? UnpairLink);
