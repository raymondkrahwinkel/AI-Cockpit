using System.IO.Compression;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Install-from-zip validation + safe extraction + removal sweep for the plugin installer (#14).</summary>
public class PluginInstallerTests : IDisposable
{
    private const int HostMajor = 1;

    private readonly string _tempDir;
    private readonly string _pluginsRoot;
    private readonly PluginInstaller _installer;

    public PluginInstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-plugin-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _pluginsRoot = Path.Combine(_tempDir, "plugins");
        _installer = new PluginInstaller(_pluginsRoot);
    }

    // AC-1159: entryAssembly is manifest data, not a zip entry name -- PluginInstallPath's zip-slip guard
    // (above) never sees it. Left unchecked, `Path.Combine(stagingDir, "../approved-plugin/entry.dll")`
    // walks out of staging and hashes/installs over an already-approved sibling's real assembly instead.
    [Fact]
    public async Task InstallFromZipAsync_EntryAssemblyOrZipEntryEscapingViaDotDot_Rejected()
    {
        await _installer.InstallFromZipAsync(_PluginZip("approved-plugin", dll: "MZ-approved"), HostMajor);
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("victim", "Victim", "../approved-plugin/Plugin.dll", abstractionsVersion: 1),
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "victim")));

        // And the zip-slip guard itself: a crafted entry escaping the destination is refused before anything lands.
        var slip = await _installer.InstallFromZipAsync(_CreateZip(new() { ["../escape.txt"] = "pwned" }), HostMajor);

        Assert.False(slip.IsSuccess);
        Assert.False(File.Exists(Path.Combine(_tempDir, "escape.txt")));
    }

    private string _PluginZip(string id, string dll) => _CreateZip(new()
    {
        ["plugin.json"] = _Manifest(id, id, "Plugin.dll", abstractionsVersion: 1),
        ["Plugin.dll"] = dll,
    });

    private static string _Manifest(string id, string name, string entryAssembly, int abstractionsVersion, string? minHostVersion = null) =>
        minHostVersion is null
            ? $$"""{"id":"{{id}}","name":"{{name}}","version":"1.0.0","entryAssembly":"{{entryAssembly}}","abstractionsVersion":{{abstractionsVersion}}}"""
            : $$"""{"id":"{{id}}","name":"{{name}}","version":"1.0.0","entryAssembly":"{{entryAssembly}}","abstractionsVersion":{{abstractionsVersion}},"minHostVersion":"{{minHostVersion}}"}""";

    private string _CreateZip(Dictionary<string, string> entries)
    {
        var zipPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
