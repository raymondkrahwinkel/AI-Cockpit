using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2222: the pairing QR code (PROTOCOL.md § 2), single use and five minutes long. PairingSecret leaves the machine only
// as the QR code: never stored, logged or sent, and wiped once the code pairs a phone, expires or is discarded. Depot
// only ever sees PairingId, which it issued (DEP-628).
public sealed class PairingPayload(string depot, string pairingId, byte[] cockpitPub, byte[] pairingSecret, DateTimeOffset expiresAt)
{
    public const int Version = 1;
    public const int SecretBytes = 32;

    // The same five minutes Depot gives the pairing id (DEP-628); the Cockpit does not rely on Depot to enforce them.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private int _closed;

    public string Depot { get; } = depot;

    public string PairingId { get; } = pairingId;

    public byte[] CockpitPub { get; } = cockpitPub;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    internal byte[] PairingSecret { get; } = pairingSecret;

    public static PairingPayload Create(string depotUrl, string pairingId, byte[] cockpitPub) =>
        new(depotUrl, pairingId, cockpitPub, RandomNumberGenerator.GetBytes(SecretBytes), DateTimeOffset.UtcNow + Lifetime);

    // base64url (no padding) of the UTF-8 JSON {v, depot, pairingId, cockpitPub, pairingSecret}, the two keys base64url too.
    public string Encode()
    {
        if (!IsOpen())
        {
            throw new InvalidOperationException("This pairing code has been used or has expired.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = Version,
            ["depot"] = Depot,
            ["pairingId"] = PairingId,
            ["cockpitPub"] = Base64Url.EncodeToString(CockpitPub),
            ["pairingSecret"] = Base64Url.EncodeToString(PairingSecret),
        });
        var encoded = Base64Url.EncodeToString(json);
        CryptographicOperations.ZeroMemory(json);
        return encoded;
    }

    // Not used yet and not expired. An expired code is discarded on the spot, so its secret does not outlive it.
    public bool IsOpen()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow < ExpiresAt)
        {
            return true;
        }

        Discard();
        return false;
    }

    // Compare-and-set: of two handshakes that both carried the right MAC, exactly one gets the code.
    internal bool TryUse()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(PairingSecret);
        return true;
    }

    // The code is closed for good: the operator closed the dialog, or it expired.
    public void Discard()
    {
        Interlocked.Exchange(ref _closed, 1);
        CryptographicOperations.ZeroMemory(PairingSecret);
    }
}
