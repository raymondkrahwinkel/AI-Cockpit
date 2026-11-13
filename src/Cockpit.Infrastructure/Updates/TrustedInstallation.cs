using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cockpit.Infrastructure.Updates;

// Readiness for code the vault may trust (AC-2342): admin-only ACLs up the tree, exact SHA256SUMS, pinned signer.
public static class TrustedInstallation
{
    public const string ManifestName = "SHA256SUMS";

    // ponytail: empty until a signing certificate exists; until then no installation is trusted with a real vault.
    public static readonly IReadOnlyList<string> PinnedPublisherSha256 = [];

    private static readonly string[] LoaderOverrideVariables =
    [
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT", "DOTNET_ROLL_FORWARD",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "COMPlus_EnableDiagnostics",
        "DOTNET_EnableDiagnostics", "LD_PRELOAD", "LD_LIBRARY_PATH",
    ];

    public static TrustedInstallReport Verify(
        string root,
        IReadOnlyCollection<string> signedEntries,
        IReadOnlyCollection<string> pinnedPublisherSha256)
    {
        var failures = new List<string>();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return new TrustedInstallReport(["platform-unsupported"]);
        }

        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            return new TrustedInstallReport(["install-root-missing"]);
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var name in LoaderOverrideVariables)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                failures.Add($"loader-override-env:{name}");
            }
        }

        if (_IsUnderUserLocation(root))
        {
            failures.Add("install-root-in-user-location");
        }

        _CheckAncestors(root, failures);
        var listed = _ReadManifest(root, failures);
        _CheckTree(root, listed, failures);
        _CheckSignatures(root, listed, signedEntries, pinnedPublisherSha256, failures);

        return new TrustedInstallReport(failures);
    }

    private static bool _IsUnderUserLocation(string root)
    {
        string[] userLocations =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(),
        ];
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return userLocations
            .Where(location => !string.IsNullOrEmpty(location))
            .Select(location => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)))
            .Any(location => root.Equals(location, comparison)
                || root.StartsWith(location + Path.DirectorySeparatorChar, comparison));
    }

    // A parent the agent can rename, re-own or re-ACL lets it swap the whole tree, so every ancestor is checked too.
    private static void _CheckAncestors(string root, List<string> failures)
    {
        for (var current = Directory.GetParent(root); current is not null; current = current.Parent)
        {
            var name = $"ancestor:{current.FullName}";
            if (current.Parent is not null && current.LinkTarget is not null)
            {
                failures.Add($"reparse-point:{name}");
            }

            _CheckEntry(current.FullName, name, ancestor: true, failures);
        }
    }

    private static Dictionary<string, string> _ReadManifest(string root, List<string> failures)
    {
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(root, ManifestName);
        if (!File.Exists(path))
        {
            failures.Add("manifest-missing");

            return listed;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            // sha256sum format: 64 hex, a space, a space or '*', then the relative path.
            if (line.Length < 67 || line[64] != ' ' || (line[65] != ' ' && line[65] != '*')
                || !line[..64].All(char.IsAsciiHexDigit))
            {
                failures.Add($"manifest-invalid-line:{lineNumber}");

                continue;
            }

            var entry = line[66..].Replace('\\', '/');
            if (Path.IsPathRooted(entry) || entry.Split('/').Any(part => part is "" or "." or "..")
                || entry == ManifestName || !listed.TryAdd(entry, line[..64].ToLowerInvariant()))
            {
                failures.Add($"manifest-invalid-entry:{lineNumber}");
            }
        }

        return listed;
    }

    private static void _CheckTree(string root, Dictionary<string, string> listed, List<string> failures)
    {
        _CheckEntry(root, ".", ancestor: false, failures);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in _Walk(new DirectoryInfo(root)))
        {
            var entry = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failures.Add($"reparse-point:{entry}");

                continue;
            }

            _CheckEntry(info.FullName, entry, ancestor: false, failures);
            if (info is DirectoryInfo || entry == ManifestName)
            {
                continue;
            }

            seen.Add(entry);
            if (!listed.TryGetValue(entry, out var expected))
            {
                failures.Add($"unlisted-file:{entry}");
            }
            else if (!_Sha256(info.FullName).Equals(expected, StringComparison.Ordinal))
            {
                failures.Add($"hash-mismatch:{entry}");
            }
        }

        failures.AddRange(listed.Keys.Where(entry => !seen.Contains(entry)).Select(entry => $"missing-file:{entry}"));
    }

    // Recurses by hand: the built-in recursive enumeration descends into junctions, which must stay unvisited.
    private static IEnumerable<FileSystemInfo> _Walk(DirectoryInfo directory)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var info in directory.EnumerateFileSystemInfos("*", options))
        {
            yield return info;
            if (info is DirectoryInfo child && info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var nested in _Walk(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void _CheckSignatures(
        string root,
        Dictionary<string, string> listed,
        IReadOnlyCollection<string> signedEntries,
        IReadOnlyCollection<string> pinnedPublisherSha256,
        List<string> failures)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (pinnedPublisherSha256.Count == 0)
        {
            failures.Add("no-pinned-publisher");

            return;
        }

        foreach (var entry in signedEntries)
        {
            if (!listed.ContainsKey(entry))
            {
                failures.Add($"signed-entry-not-in-manifest:{entry}");

                continue;
            }

            var signer = AuthenticodeSignature.VerifiedSignerSha256(Path.Combine(root, entry));
            if (signer is null)
            {
                failures.Add($"signature-invalid:{entry}");
            }
            else if (!pinnedPublisherSha256.Contains(signer, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"signer-not-pinned:{entry}");
            }
        }
    }

    private static void _CheckEntry(string path, string name, bool ancestor, List<string> failures)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsInstallAcl.Check(path, name, ancestor, failures);
        }
        else
        {
            LinuxInstallOwnership.Check(path, name, ancestor, failures);
        }
    }

    private static string _Sha256(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

public sealed record TrustedInstallReport(IReadOnlyList<string> Failures)
{
    public bool IsTrusted => Failures.Count == 0;
}

// Linux: root owns the entry, nobody else may write it, and inside the tree no file is a hard link (AC-2342).
internal static class LinuxInstallOwnership
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxModeUidNlink = 0x2 | 0x4 | 0x8;

    public static void Check(string path, string name, bool ancestor, List<string> failures)
    {
        var buffer = new byte[256];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxModeUidNlink, buffer) != 0)
        {
            failures.Add($"unreadable:{name}");

            return;
        }

        var nlink = BitConverter.ToUInt32(buffer, 16);
        var uid = BitConverter.ToUInt32(buffer, 20);
        var mode = (UnixFileMode)(BitConverter.ToUInt16(buffer, 28) & 0xFFF);
        if (uid != 0)
        {
            failures.Add($"untrusted-owner:{name}");
        }

        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            failures.Add($"untrusted-write-access:{name}");
        }

        const ushort directoryType = 0x4000;
        if (!ancestor && nlink > 1 && (BitConverter.ToUInt16(buffer, 28) & 0xF000) != directoryType)
        {
            failures.Add($"hard-link:{name}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, byte[] buffer);
}
