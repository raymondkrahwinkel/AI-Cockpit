using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Configuration;

namespace Cockpit.Core.Sessions;

public static class SessionTempDirectory
{
    public static string Root => Path.Combine(Path.GetTempPath(), "cockpit", _Fingerprint(CockpitBuild.StateRoot));

    public static string Create(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || paneId is "." or ".."
            || paneId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || paneId.Contains('/') || paneId.Contains('\\'))
        {
            throw new ArgumentException("A pane id must be a single directory name.", nameof(paneId));
        }

        var path = Path.Combine(Root, paneId);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string _Fingerprint(string stateRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToLowerInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }
}
