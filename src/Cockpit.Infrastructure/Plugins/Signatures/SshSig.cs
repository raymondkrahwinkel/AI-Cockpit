using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

// DEP-564: the whole .NET side of the contract, as Depot and Cockpit each hold it, byte for byte. cases.json is what keeps
// the copies honest. BouncyCastle only for Ed25519 itself.
public static partial class SshSig
{
    public const string Namespace = "cockpit-plugin-v1@krahwinkel-it.nl";
    private const string KeyType = "ssh-ed25519";
    private const string HashName = "sha512";
    private const string Begin = "-----BEGIN SSH SIGNATURE-----";
    private const string End = "-----END SSH SIGNATURE-----";

    // libsodium's ge25519_has_small_order: compared with the sign bit cleared.
    private static readonly HashSet<string> SmallOrder =
    [
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0100000000000000000000000000000000000000000000000000000000000000",
        "26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc05",
        "c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a",
        "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
    ];

    public sealed record Check(bool Ok, string? Reason, string? Fingerprint);

    // The input rules: ASCII classes and \z, never \d or $, which in .NET take Unicode digits and stop before a final LF.
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,62}\z")]
    private static partial Regex OrgPattern();

    [GeneratedRegex(@"\A[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A(any|[a-z0-9][a-z0-9.-]{0,31})\z")]
    private static partial Regex PlatformPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"\A[\x21-\x7e]{0,200}\z")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"\A[\x21-\x7e]{0,100}\z")]
    private static partial Regex TagPattern();

    public static bool IsValidId(string id) => id.Length <= 100 && IdPattern().IsMatch(id);

    public static bool IsValidVersion(string version) => VersionPattern().IsMatch(version);

    public static byte[] Statement(string org, string id, string version, string platform, string sha256, string repository, string tag)
    {
        if (!OrgPattern().IsMatch(org)) throw new ArgumentException("org");
        if (!IsValidId(id)) throw new ArgumentException("id");
        if (!IsValidVersion(version)) throw new ArgumentException("version");
        if (!PlatformPattern().IsMatch(platform)) throw new ArgumentException("platform");
        if (!Sha256Pattern().IsMatch(sha256)) throw new ArgumentException("sha256");
        if (!RepositoryPattern().IsMatch(repository)) throw new ArgumentException("repository");
        if (!TagPattern().IsMatch(tag)) throw new ArgumentException("tag");
        if (repository.Length == 0 != (tag.Length == 0)) throw new ArgumentException("repository and tag go together");

        // ASCII by the rules above, so there is exactly one encoding of it.
        return Encoding.ASCII.GetBytes($"org={org}\nid={id}\nversion={version}\nplatform={platform}\nsha256={sha256}\nrepository={repository}\ntag={tag}\n");
    }

    public static bool IsWeakKey(byte[] key)
    {
        var masked = (byte[])key.Clone();
        masked[31] &= 0x7f;
        return SmallOrder.Contains(Convert.ToHexStringLower(masked));
    }

    public static string FingerprintOf(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(PublicBlob(key))).TrimEnd('=');

    public static Check Verify(byte[] message, string armored)
    {
        try
        {
            var text = armored.Replace("\r\n", "\n").Trim();
            if (!text.StartsWith(Begin + "\n", StringComparison.Ordinal) || !text.EndsWith("\n" + End, StringComparison.Ordinal))
                return new(false, "armor", null);

            var reader = new Reader(Convert.FromBase64String(text[Begin.Length..^End.Length].Replace("\n", "")));
            if (Encoding.ASCII.GetString(reader.Take(6)) != "SSHSIG" || reader.UInt32() != 1)
                return new(false, "magic", null);

            var keyBlob = new Reader(reader.Bytes());
            if (keyBlob.Text() != KeyType)
                return new(false, "keytype", null);
            var key = keyBlob.Bytes();
            keyBlob.End();
            if (key.Length != 32)
                return new(false, "keylength", null);

            var ns = reader.Text();
            if (ns != Namespace)
                return new(false, "namespace", null);
            if (reader.Bytes().Length != 0)
                return new(false, "reserved", null);
            var hash = reader.Text();
            if (hash != HashName)
                return new(false, "hash", null);

            var signature = new Reader(reader.Bytes());
            if (signature.Text() != KeyType)
                return new(false, "sigtype", null);
            var raw = signature.Bytes();
            signature.End();
            reader.End();
            if (raw.Length != 64)
                return new(false, "siglength", null);
            if (IsWeakKey(key))
                return new(false, "weak-key", null);

            return RawVerify(key, raw, SignedData(message))
                ? new(true, null, FingerprintOf(key))
                : new(false, "signature", null);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
        {
            return new(false, $"malformed: {exception.Message}", null);
        }
    }

    public static string Sign(byte[] seed, byte[] publicKey, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        var data = SignedData(message);
        signer.BlockUpdate(data, 0, data.Length);
        var raw = signer.GenerateSignature();

        using var blob = new MemoryStream();
        blob.Write("SSHSIG"u8);
        blob.Write(BigEndian(1));
        WriteString(blob, PublicBlob(publicKey));
        WriteString(blob, Encoding.ASCII.GetBytes(Namespace));
        WriteString(blob, []);
        WriteString(blob, Encoding.ASCII.GetBytes(HashName));
        using var inner = new MemoryStream();
        WriteString(inner, Encoding.ASCII.GetBytes(KeyType));
        WriteString(inner, raw);
        WriteString(blob, inner.ToArray());

        var body = Convert.ToBase64String(blob.ToArray());
        var lines = Enumerable.Range(0, (body.Length + 69) / 70).Select(i => body.Substring(i * 70, Math.Min(70, body.Length - i * 70)));
        return $"{Begin}\n{string.Join('\n', lines)}\n{End}\n";
    }

    public static bool RawVerify(byte[] key, byte[] raw, byte[] data)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(raw);
    }

    private static byte[] SignedData(byte[] message)
    {
        using var data = new MemoryStream();
        data.Write("SSHSIG"u8);
        WriteString(data, Encoding.ASCII.GetBytes(Namespace));
        WriteString(data, []);
        WriteString(data, Encoding.ASCII.GetBytes(HashName));
        WriteString(data, SHA512.HashData(message));
        return data.ToArray();
    }

    public static byte[] PublicBlob(byte[] key)
    {
        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes(KeyType));
        WriteString(blob, key);
        return blob.ToArray();
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void WriteString(Stream stream, byte[] value)
    {
        stream.Write(BigEndian(value.Length));
        stream.Write(value);
    }

    private sealed class Reader(byte[] data)
    {
        private int _at;

        public byte[] Take(int count)
        {
            if (count < 0 || _at + count > data.Length)
                throw new InvalidDataException("truncated");
            var result = data[_at..(_at + count)];
            _at += count;
            return result;
        }

        public uint UInt32() => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public byte[] Bytes() => Take(checked((int)Math.Min(UInt32(), int.MaxValue)));

        public string Text() => new UTF8Encoding(false, true).GetString(Bytes());

        public void End()
        {
            if (_at != data.Length)
                throw new InvalidDataException("trailing bytes");
        }
    }
}
