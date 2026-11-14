using System.Text.Json;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.OnePassword;

internal static class OnePasswordManagedCli
{
    internal static readonly Version MinimumVersion = new(2, 18, 0);

    public static ManagedCliDescriptor Descriptor { get; } = new()
    {
        CliName = "op",
        ResolveLatestVersionAsync = async (http, ct) =>
        {
            var json = await http.GetStringAsync("https://app-updates.agilebits.com/check/1/0/CLI2/en/2.0.0/N", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return _Version(document.RootElement.GetProperty("version").GetString());
        },
        BuildDownloadPlanAsync = (_, platform, version, _) =>
        {
            version = _Version(version);
            var os = platform.Os switch
            {
                "win32" => "windows",
                "linux" => "linux",
                "darwin" => "darwin",
                _ => throw new InvalidOperationException($"1Password CLI has no build for '{platform.Os}'."),
            };
            var arch = platform.Arch switch
            {
                "x64" => "amd64",
                "arm64" => "arm64",
                _ => throw new InvalidOperationException($"1Password CLI has no build for '{platform.Arch}'."),
            };
            if (os == "windows" && arch == "arm64")
            {
                throw new InvalidOperationException("1Password does not publish a Windows ARM64 CLI ZIP.");
            }
            var executable = os == "windows" ? "op.exe" : "op";
            return Task.FromResult(new ManagedCliDownloadPlan
            {
                Url = $"https://cache.agilebits.com/dist/1P/op2/pkg/v{version}/op_{os}_{arch}_v{version}.zip",
                ExpectedSha256 = string.Empty,
                VerifySignatureAsync = OnePasswordDownloadVerifier.VerifyAsync,
                ExecutableFileName = executable,
                ExecutableEntryName = executable,
                ArchiveFormat = ManagedCliArchiveFormat.Zip,
                NeedsExecutableBit = os != "windows",
            });
        },
    };

    private static string _Version(string? text) =>
        Version.TryParse(text, out var version) && version >= MinimumVersion && version.Revision < 0
            ? version.ToString()
            : throw new InvalidOperationException("1Password CLI reported an invalid version; version 2.18.0 or later is required.");
}
