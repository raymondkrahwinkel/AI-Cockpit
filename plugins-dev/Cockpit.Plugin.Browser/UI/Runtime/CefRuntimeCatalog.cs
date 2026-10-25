using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cockpit.Plugin.Browser.UI.Runtime;

internal sealed record CefRuntimePackage(string Url, long Size, long UnpackedSize, string Sha256)
{
    // The text for the prompt before the first download. Both sizes, since the Linux runtime is not stripped
    // and takes about five times its download on disk.
    public string DownloadText => $"Browser engine: download ≈ {_Format(Size)}, ≈ {_Format(UnpackedSize)} on disk";

    private static string _Format(long bytes) => bytes >= 1_000_000_000
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1e9:F1} GB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1_000_000} MB");
}

// The one CEF this plugin version works with, per platform (AC-1727). It is embedded in the UI assembly, so the
// plugin's own hash covers the URLs and checksums: trusting the plugin means trusting exactly these archives.
// `scripts/generate-cef-catalog.sh` rewrites the JSON; it moves together with the CefGlue.Next.Core version.
internal sealed record CefRuntimeCatalog(
    string CefVersion,
    string ChromiumVersion,
    DateOnly BuildDate,
    IReadOnlyDictionary<string, CefRuntimePackage> Packages)
{
    // Older than this and the plugin settings say so; it never blocks anything.
    public const int OutdatedAfterDays = 60;

    public static CefRuntimeCatalog Load()
    {
        using var stream = typeof(CefRuntimeCatalog).Assembly.GetManifestResourceStream("cef-catalog.json")
            ?? throw new InvalidOperationException("The CEF catalog is not embedded in the browser plugin.");

        return JsonSerializer.Deserialize<CefRuntimeCatalog>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The CEF catalog is empty.");
    }

    public bool IsOutdated(DateOnly today) => today.DayNumber - BuildDate.DayNumber > OutdatedAfterDays;

    // The catalog key for the machine we run on, or null where CEF publishes no build (e.g. 32-bit).
    public static string? CurrentRid()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (architecture is null)
        {
            return null;
        }

        return OperatingSystem.IsWindows() ? $"win-{architecture}"
            : OperatingSystem.IsMacOS() ? $"osx-{architecture}"
            : OperatingSystem.IsLinux() ? $"linux-{architecture}"
            : null;
    }
}
