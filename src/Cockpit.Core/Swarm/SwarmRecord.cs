using System.Security.Cryptography;
using System.Text.Json;

namespace Cockpit.Core.Swarm;

// AC-1915: the signed envelope every swarm record travels in. Each signature is ECDSA P-256 over a fixed context
// plus the payload's canonical bytes (`SwarmRecordJson.Canonicalize`); the envelope carries no public key, only the
// member id.
public sealed record SwarmRecord(JsonElement Payload, IReadOnlyList<SwarmSignature> Signatures)
{
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    // The node key is also the TLS key; signing behind a fixed context separates swarm signatures from TLS ones.
    private static ReadOnlySpan<byte> Context => "cockpit-swarm-record-v1\n"u8;

    public static SwarmRecord Sign(JsonElement payload, ECDsa key)
    {
        var signature = key.SignData(_SignedBytes(SwarmRecordJson.Canonicalize(payload)), HashAlgorithmName.SHA256, Format);
        var memberId = SwarmMemberId.FromPublicKey(key.ExportSubjectPublicKeyInfo());
        return new SwarmRecord(payload.Clone(), [new SwarmSignature(memberId, signature)]);
    }

    // True when there is at least one signature and every one checks out against the key `publicKeyOf` knows for
    // its member. That key must hash to the member id, so a key handed over with a record cannot vouch for an id
    // it does not belong to.
    public bool Verify(Func<SwarmMemberId, byte[]?> publicKeyOf)
    {
        if (Signatures.Count == 0 || !SwarmRecordJson.TryCanonicalize(Payload, out var canonical))
        {
            return false;
        }

        var signed = _SignedBytes(canonical);

        foreach (var signature in Signatures)
        {
            if (publicKeyOf(signature.MemberId) is not { } publicKey || SwarmMemberId.FromPublicKey(publicKey) != signature.MemberId)
            {
                return false;
            }

            using var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(publicKey, out _);
            }
            catch (CryptographicException)
            {
                return false;
            }

            if (!key.VerifyData(signed, signature.Signature, HashAlgorithmName.SHA256, Format))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] _SignedBytes(byte[] canonical) => [.. Context, .. canonical];
}

public sealed record SwarmSignature(SwarmMemberId MemberId, byte[] Signature);
