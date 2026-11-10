using System.Buffers.Text;
using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Secrets;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2222: the Cockpit's P-256 pair per Depot connection and its paired phones, in the plugin's settings, which the sync
// never carries; key and wrapping key are declared secrets (PROTOCOL.md § 7 says what that does and does not protect).
// An unreadable key is reported, never silently replaced; one lock for every instance, so no two pairs get created.
public sealed class MobileKeyStore(IPluginStorage storage)
{
    private const string WrapKeyName = "mobileKeyWrapSecret";
    private const byte UncompressedPoint = 0x04;
    private const int CoordinateBytes = 32;
    private const int WrapKeyBytes = 32;

    private static readonly Lock Gate = new();

    // True with the key (the caller owns and disposes it), created on first use. False when a key is stored but cannot be
    // read — a missing or different wrapping key, a damaged value, or phones paired to a key that is gone — and nothing
    // was changed: the caller tells the operator, who can Reset.
    public bool TryGetCockpitKey(string connectionId, out ECDiffieHellman? key)
    {
        lock (Gate)
        {
            key = null;
            var wrapped = storage.GetSecret(_KeyName(connectionId));
            if (string.IsNullOrEmpty(wrapped))
            {
                if (_Phones(connectionId).Count > 0)
                {
                    return false;
                }

                key = _Create(connectionId);
                return true;
            }

            var wrapKey = _WrapKey();
            if (wrapKey is null || !ProjectSecretProtector.IsProtected(wrapped))
            {
                return false;
            }

            byte[]? pkcs8 = null;
            try
            {
                pkcs8 = Convert.FromBase64String(new ProjectSecretProtector(wrapKey).Unprotect(_Path(connectionId), wrapped));
                var imported = ECDiffieHellman.Create();
                try
                {
                    imported.ImportPkcs8PrivateKey(pkcs8, out _);
                }
                catch
                {
                    imported.Dispose();
                    throw;
                }
                key = imported;
                return true;
            }
            catch (Exception exception) when (exception is ProjectSecretProtectionException or FormatException or CryptographicException)
            {
                return false;
            }
            finally
            {
                if (pkcs8 is not null)
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
    }

    // A new pair for this connection, forgetting every phone paired to the old one: each has to pair again.
    public ECDiffieHellman ResetCockpitKey(string connectionId)
    {
        lock (Gate)
        {
            storage.Set(_PhonesName(connectionId), new List<string>());
            return _Create(connectionId);
        }
    }

    public bool IsPaired(string connectionId, byte[] phonePub)
    {
        lock (Gate)
        {
            return _Phones(connectionId).Contains(Base64Url.EncodeToString(phonePub));
        }
    }

    public void AddPhone(string connectionId, byte[] phonePub)
    {
        lock (Gate)
        {
            var phones = _Phones(connectionId);
            var phone = Base64Url.EncodeToString(phonePub);
            if (!phones.Contains(phone))
            {
                storage.Set(_PhonesName(connectionId), phones.Append(phone).ToList());
            }
        }
    }

    // Revokes one phone; the Cockpit's pair and the other phones stay.
    public void RemovePhone(string connectionId, byte[] phonePub)
    {
        lock (Gate)
        {
            var phone = Base64Url.EncodeToString(phonePub);
            storage.Set(_PhonesName(connectionId), _Phones(connectionId).Where(paired => paired != phone).ToList());
        }
    }

    // The 65-byte uncompressed SEC1 point 0x04 ‖ X ‖ Y, the one form the protocol puts on the wire.
    public static byte[] PublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return [UncompressedPoint, .. q.X!, .. q.Y!];
    }

    // Throws CryptographicException for anything that is not a point on P-256: an off-curve point fed to ECDH is how an
    // invalid-curve attack reads a static private key, so it has to be refused before the agreement, not after.
    public static ECDiffieHellman ImportPublicKey(byte[] publicKey)
    {
        if (publicKey.Length != 1 + 2 * CoordinateBytes || publicKey[0] != UncompressedPoint)
        {
            throw new CryptographicException("Not an uncompressed P-256 public key.");
        }

        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[1..(1 + CoordinateBytes)], Y = publicKey[(1 + CoordinateBytes)..] },
            });
            return key;
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            // Windows CNG answers an off-curve point with PlatformNotSupportedException, OpenSSL with CryptographicException.
            key.Dispose();
            throw new CryptographicException("Not a point on P-256.");
        }
    }

    private ECDiffieHellman _Create(string connectionId)
    {
        var wrapKey = _WrapKey();
        if (wrapKey is null)
        {
            wrapKey = RandomNumberGenerator.GetBytes(WrapKeyBytes);
            storage.SetSecret(WrapKeyName, Base64Url.EncodeToString(wrapKey));
        }

        var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = created.ExportPkcs8PrivateKey();
        storage.SetSecret(_KeyName(connectionId), new ProjectSecretProtector(wrapKey).Protect(_Path(connectionId), Convert.ToBase64String(pkcs8)));
        CryptographicOperations.ZeroMemory(pkcs8);
        CryptographicOperations.ZeroMemory(wrapKey);
        return created;
    }

    private byte[]? _WrapKey()
    {
        var stored = storage.GetSecret(WrapKeyName);
        return stored is not null && Base64Url.IsValid(stored, out var length) && length == WrapKeyBytes
            ? Base64Url.DecodeFromChars(stored)
            : null;
    }

    private List<string> _Phones(string connectionId) => storage.Get<List<string>>(_PhonesName(connectionId)) ?? [];

    private static string _KeyName(string connectionId) => $"mobileCockpitKeySecret-{connectionId}";

    private static string _PhonesName(string connectionId) => $"mobilePhones-{connectionId}";

    // The protector's associated data: a wrapped key copied to another connection's slot does not open there.
    private static string _Path(string connectionId) => $"mobile/{connectionId}/cockpitKey";
}
