using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Plugins;

// SHA-256 of a plugin's bytes, hex-encoded — pinned in `cockpit.json` so a changed or tampered plugin re-triggers the consent prompt.
public static class PluginHash
{
    public static string Compute(ReadOnlySpan<byte> assemblyBytes) => Convert.ToHexStringLower(SHA256.HashData(assemblyBytes));

    // Folds a plugin's whole load closure into one pin (AC-43). Pinning only the entry assembly re-triggered
    // consent when the entry DLL changed but not when a sibling *dependency* DLL was swapped — and those
    // are loaded in-process with full trust (`PluginLoadContext` resolves them from the folder), so a tamper
    // or a store update that kept the entry byte-identical ran unconsented code. Each file contributes its
    // forward-slash relative path and its own SHA-256, ordered by path so the pin is independent of enumeration
    // order and platform separator. Any changed byte in any file — or a moved/renamed file — changes the pin, so
    // the consent prompt returns. Pure: the caller does the file IO.
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
