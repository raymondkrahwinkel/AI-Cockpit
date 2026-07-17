using System.Runtime.InteropServices;

namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// The host's provider-agnostic view of the machine a managed CLI is being installed for (AC-20): the OS family,
/// the CPU architecture, and — on Linux — whether the C library is musl rather than glibc. The core never names a
/// download key or a target-triple; a <see cref="ManagedCliDescriptor"/> maps this generic triple to whatever key
/// its provider's release channel uses (Claude's <c>linux-x64</c>/<c>darwin-arm64</c>, Codex's
/// <c>x86_64-unknown-linux-musl</c>, …), so the same installer serves every provider unchanged.
/// </summary>
/// <param name="Os">The OS family: <c>win32</c>, <c>darwin</c> or <c>linux</c> — the vocabulary Claude's manifest uses.</param>
/// <param name="Arch">The CPU architecture: <c>x64</c>, <c>arm64</c>, or the lowercased <see cref="Architecture"/> name for anything else.</param>
/// <param name="IsMusl">True on a Linux machine whose C library is musl (Alpine and similar); always false off Linux.</param>
public readonly record struct ManagedCliPlatform(string Os, string Arch, bool IsMusl)
{
    /// <summary>Reads the running machine's platform. Cheap and side-effect-free — call it per install rather than caching.</summary>
    public static ManagedCliPlatform Current()
    {
        var os = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "linux";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        return new ManagedCliPlatform(os, arch, os == "linux" && _IsMuslLibc());
    }

    // The musl loader lives at /lib/ld-musl-<arch>.so.1 on Alpine and other musl distros; its presence is what the
    // official claude/codex install scripts also key musl-detection on. Guarded against a missing /lib so a locked-down
    // layout degrades to "glibc" rather than throwing during platform discovery.
    private static bool _IsMuslLibc()
    {
        try
        {
            return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
