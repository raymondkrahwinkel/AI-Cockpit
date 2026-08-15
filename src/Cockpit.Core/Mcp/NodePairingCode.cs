using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Mcp;

// The short number both screens show during a pairing (AC-792, open point 2: compare rather than type).
//
// The point of comparing rather than typing is that the code never has to travel over a channel the operator
// controls — no clipboard, no chat window, nothing to intercept. That only buys anything if the two sides can
// arrive at the same number *independently*, which is why this derives it instead of one side sending it:
//
//   * the node computes it from the nonce it minted and **its own** certificate's fingerprint;
//   * the controller computes it from the same nonce and the fingerprint of the certificate it **actually saw
//     on the wire**.
//
// A machine sitting in the middle — terminating TLS towards the controller and reopening it towards the node —
// forwards the nonce unchanged but cannot forward a fingerprint it does not have the private key for. The two
// numbers then differ, and the human comparing them is the one who notices. Sending the code instead would have
// let that same machine forward it verbatim and the comparison would have proved nothing but connectivity.
//
// Six digits is a one-in-a-million chance of a collision surviving one attempt, against a window (two minutes)
// in which the operator is standing at both machines and a mismatch is a visible refusal, not a silent retry.
// ponytail: truncated hash, not an SAS over a fresh key exchange — the certificate is the long-lived key here,
// so there is nothing extra to agree on. Upgrade path is a real short-authentication-string if the node ever
// gets an identity that is not its TLS certificate.
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
