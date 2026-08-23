using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Mcp;

// AC-792 (open point 2): the short number both screens show, compared rather than typed so it never travels
// over an interceptable channel. Each side derives it independently from the same nonce plus the certificate
// it saw — a MITM forwards the nonce but can't forge the fingerprint, so the codes mismatch.
public static class NodePairingCode
{
    public const int Digits = 6;

    private const int Modulus = 1_000_000;

    // Domain separation, so this hash can never collide with another use of the same nonce elsewhere.
    private const string Label = "cockpit-node-pairing-v1";

    public static string Derive(string nonce, string certificateFingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentException.ThrowIfNullOrEmpty(certificateFingerprint);

        var material = Encoding.UTF8.GetBytes($"{Label}\n{nonce}\n{Normalize(certificateFingerprint)}");
        var digest = SHA256.HashData(material);

        // The top four bytes as an unsigned big-endian integer, folded into six digits. Reducing modulo a
        // non-power-of-two skews the distribution by about one part in four thousand — irrelevant against a
        // code whose job is to be compared by eye once, and not worth a rejection-sampling loop.
        var value = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
        return (value % Modulus).ToString($"D{Digits}", System.Globalization.CultureInfo.InvariantCulture);
    }

    // The same certificate written two ways ("AA:BB" and "aabb") must give the same code: .NET hands the
    // fingerprint back in one shape and an operator reading it off a dialog may well see the other.
    public static string Normalize(string certificateFingerprint) =>
        certificateFingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}
