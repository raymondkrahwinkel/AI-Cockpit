using System.Security.Cryptography;

namespace Cockpit.Plugin.Depot.Mobile;

internal static class MobileKeyFingerprint
{
    public static string Format(byte[] cockpitPub, byte[] phonePub)
    {
        byte[] digest = SHA256.HashData([.. cockpitPub, .. phonePub]);
        string hex = Convert.ToHexString(digest);
        return string.Join(" ", Enumerable.Range(0, digest.Length / 2).Select(group => hex.Substring(group * 4, 4)));
    }
}
