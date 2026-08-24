using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Plugins;

// SHA-256 of a plugin's bytes, hex-encoded — pinned in `cockpit.json` so a changed or tampered plugin re-triggers the consent prompt.
public static class PluginHash
{
    public static string Compute(ReadOnlySpan<byte> assemblyBytes) => Convert.ToHexStringLower(SHA256.HashData(assemblyBytes));

    // AC-43: Pins the whole load closure, not just the entry assembly — pinning only the entry let a
    // swapped dependency DLL run unconsented, since dependencies load in-process with full trust too.
    // (Omitted: path/hash framing and ordering rationale, the "pure, caller does IO" note; see ticket.)
    public static string ComputeClosure(IEnumerable<PluginClosureFile> files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files
            .Select(file => (Path: file.RelativePath.Replace('\\', '/'), file.Sha256))
            .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            // Length-prefix each field rather than joining with a delimiter: a Unix path may contain any byte but
            // '/' and NUL — a newline included — so a crafted filename could otherwise forge or merge entries and
            // collide two different closures against the pin. Framed lengths make the encoding unambiguous.
            _AppendFramed(digest, Encoding.UTF8.GetBytes(file.Path));
            _AppendFramed(digest, Encoding.UTF8.GetBytes(file.Sha256));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void _AppendFramed(IncrementalHash digest, byte[] data)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        digest.AppendData(length);
        digest.AppendData(data);
    }
}

// One file in a plugin's load closure: its path relative to the plugin folder and the SHA-256 of its bytes.
public readonly record struct PluginClosureFile(string RelativePath, string Sha256);
