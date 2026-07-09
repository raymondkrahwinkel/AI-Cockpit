using System.Security.Cryptography;

namespace Cockpit.Core.Plugins;

/// <summary>SHA-256 of a plugin's entry assembly, hex-encoded — pinned in <c>cockpit.json</c> so a changed or tampered assembly re-triggers the consent prompt.</summary>
public static class PluginHash
{
    public static string Compute(ReadOnlySpan<byte> assemblyBytes) => Convert.ToHexStringLower(SHA256.HashData(assemblyBytes));
}
