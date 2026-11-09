using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2222: the Cockpit's half of the handshake (PROTOCOL.md § 3). The pairing MAC, keyed with the QR code's secret, stops
// Depot swapping the phone's key; the Cockpit's own ephemeral key makes a replayed handshake yield keys nobody recorded.
// Every refusal is the same `false`, so which check failed cannot leak.
public static class MobileHandshake
{
    public const byte Version = 1;
    public const byte FlagKnownPhone = 0;
    public const byte FlagPairing = 1;
    public const int PublicKeyBytes = 65;
    public const int MacBytes = 32;

    private const int KnownPhoneFrameBytes = 2 + 2 * PublicKeyBytes;
    private const int PairingFrameBytes = KnownPhoneFrameBytes + MacBytes;
    private const string PhoneToCockpitLabel = "cockpit-mobile v1 phone-to-cockpit";
    private const string CockpitToPhoneLabel = "cockpit-mobile v1 cockpit-to-phone";

    // `pending` is the QR code still on screen, or null when none is; `isPaired` answers for a phone key paired earlier.
    // A pairing handshake uses `pending` up (compare-and-set, the secret wiped), so of two that race, one wins; an expired
    // code is refused and wiped. The caller sends Reply then encrypted ready, and stores PhonePub only after phone ready.
    public static bool TryAccept(
        ReadOnlySpan<byte> frame,
        ECDiffieHellman cockpitKey,
        string streamId,
        PairingPayload? pending,
        Func<byte[], bool> isPaired,
        out MobileHandshakeResult? result)
    {
        // Lives for this one handshake only: once it is disposed, nobody can derive this stream's keys again.
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return TryAccept(frame, cockpitKey, ephemeral, streamId, pending, isPaired, out result);
    }

    // The test vectors' entry: the same handshake with a given Cockpit ephemeral key instead of a fresh one.
    internal static bool TryAccept(
        ReadOnlySpan<byte> frame,
        ECDiffieHellman cockpitKey,
        ECDiffieHellman cockpitEphemeral,
        string streamId,
        PairingPayload? pending,
        Func<byte[], bool> isPaired,
        out MobileHandshakeResult? result)
    {
        result = null;
        if (frame.Length < KnownPhoneFrameBytes || frame[0] != Version)
        {
            return false;
        }

        var phonePub = frame.Slice(2, PublicKeyBytes).ToArray();
        var ephPub = frame.Slice(2 + PublicKeyBytes, PublicKeyBytes).ToArray();
        bool newlyPaired;
        switch (frame[1])
        {
            case FlagKnownPhone when frame.Length == KnownPhoneFrameBytes:
                if (!isPaired(phonePub))
                {
                    return false;
                }
                newlyPaired = false;
                break;
            case FlagPairing when frame.Length == PairingFrameBytes && pending is not null && pending.IsOpen():
                var expected = PairingMac(pending.PairingSecret, pending.PairingId, phonePub);
                if (!CryptographicOperations.FixedTimeEquals(expected, frame[KnownPhoneFrameBytes..]))
                {
                    return false;
                }
                newlyPaired = true;
                break;
            default:
                return false;
        }

        if (!TryDeriveKeys(cockpitKey, cockpitEphemeral, frame[1], phonePub, ephPub, streamId, out var phoneToCockpit, out var cockpitToPhone))
        {
            return false;
        }

        // Used up only once everything else held, so a frame with a bad point cannot burn the code for the real phone.
        if (newlyPaired && !pending!.TryUse())
        {
            CryptographicOperations.ZeroMemory(phoneToCockpit);
            CryptographicOperations.ZeroMemory(cockpitToPhone);
            return false;
        }

        byte[] reply = [Version, .. MobileKeyStore.PublicKey(cockpitEphemeral)];
        result = new MobileHandshakeResult(phonePub, newlyPaired, reply, new MobileFrameCipher(cockpitToPhone, phoneToCockpit));
        CryptographicOperations.ZeroMemory(phoneToCockpit);
        CryptographicOperations.ZeroMemory(cockpitToPhone);
        return true;
    }

    // HMAC-SHA256(pairingSecret, UTF-8(pairingId) ‖ phonePub). phonePub has a fixed length, so the split is unambiguous.
    public static byte[] PairingMac(byte[] pairingSecret, string pairingId, ReadOnlySpan<byte> phonePub)
    {
        var id = Encoding.UTF8.GetBytes(pairingId);
        var message = new byte[id.Length + phonePub.Length];
        id.CopyTo(message, 0);
        phonePub.CopyTo(message.AsSpan(id.Length));
        return HMACSHA256.HashData(pairingSecret, message);
    }

    // PROTOCOL.md § 4: three ECDH terms, salt = streamId, one HKDF per direction binding version, flag and all four public
    // keys. The static terms tie the keys to the paired devices, the ephemeral-ephemeral term makes them new per handshake
    // and gives forward secrecy; the phone derives the same keys from its private halves, which the vectors check.
    private static bool TryDeriveKeys(
        ECDiffieHellman cockpitKey,
        ECDiffieHellman cockpitEphemeral,
        byte flag,
        byte[] phonePub,
        byte[] ephPub,
        string streamId,
        out byte[] phoneToCockpit,
        out byte[] cockpitToPhone)
    {
        phoneToCockpit = [];
        cockpitToPhone = [];
        byte[] ikm;
        try
        {
            using var eph = MobileKeyStore.ImportPublicKey(ephPub);
            using var phone = MobileKeyStore.ImportPublicKey(phonePub);
            var ephemeralStatic = cockpitKey.DeriveRawSecretAgreement(eph.PublicKey);
            var staticStatic = cockpitKey.DeriveRawSecretAgreement(phone.PublicKey);
            var ephemeralEphemeral = cockpitEphemeral.DeriveRawSecretAgreement(eph.PublicKey);
            ikm = [.. ephemeralStatic, .. staticStatic, .. ephemeralEphemeral];
            CryptographicOperations.ZeroMemory(ephemeralStatic);
            CryptographicOperations.ZeroMemory(staticStatic);
            CryptographicOperations.ZeroMemory(ephemeralEphemeral);
        }
        catch (CryptographicException)
        {
            // Not a point on P-256 (or not a point at all): the same refusal as a wrong MAC.
            return false;
        }

        var salt = Encoding.UTF8.GetBytes(streamId);
        byte[] keys = [Version, flag, .. MobileKeyStore.PublicKey(cockpitKey), .. MobileKeyStore.PublicKey(cockpitEphemeral), .. phonePub, .. ephPub];
        phoneToCockpit = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, [.. Encoding.ASCII.GetBytes(PhoneToCockpitLabel), .. keys]);
        cockpitToPhone = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, [.. Encoding.ASCII.GetBytes(CockpitToPhoneLabel), .. keys]);
        CryptographicOperations.ZeroMemory(ikm);
        return true;
    }
}

public sealed record MobileHandshakeResult(byte[] PhonePub, bool NewlyPaired, byte[] Reply, MobileFrameCipher Cipher);
