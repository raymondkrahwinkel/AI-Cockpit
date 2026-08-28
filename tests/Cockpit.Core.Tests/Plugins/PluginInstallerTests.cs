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

    [Fact]
    public async Task InstallFromZipAsync_ValidPlugin_UnpacksIntoNamedFolder()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("github-issues", "GitHub Issues", "Plugin.dll", abstractionsVersion: 1),
            ["Plugin.dll"] = "MZ-fake-assembly",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.True(result.IsSuccess);
        Assert.Equal("github-issues", result.FolderId);
        Assert.True(File.Exists(Path.Combine(_pluginsRoot, "github-issues", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(_pluginsRoot, "github-issues", "Plugin.dll")));
    }

    [Fact]
    public async Task InstallFromZipAsync_AbstractionsMajorMismatch_Rejected()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 2),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "x")));
    }

    // --- minHostVersion (AC-181): the same gate PluginLoadPolicy applies at load time, checked here too so a
    // too-new plugin is refused at install rather than sitting on disk reporting "installed" until the operator
    // restarts and discovers otherwise. ----------------------------------------------------------------------

    [Fact]
    public async Task InstallFromZipAsync_HostTooOld_Rejected()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1, minHostVersion: "2.0.0"),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor, hostVersion: new Version(1, 5, 0));

        Assert.False(result.IsSuccess);
        Assert.Contains("2.0.0", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "x")));
    }

    // Mutation guard: the boundary itself, not just "some too-new version is refused" — a `<` mistakenly
    // written as `<=` (or vice versa) would flip exactly this case and nothing else would catch it.
    [Fact]
    public async Task InstallFromZipAsync_HostExactlyMeetsMinHostVersion_Installs()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1, minHostVersion: "1.5.0"),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor, hostVersion: new Version(1, 5, 0));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InstallFromZipAsync_BeforeHostReachesOnePointZero_ADeclaredOnePointZeroRequirement_DoesNotBite()
    {
        // Mirrors PluginLoadPolicy's own exemption for the stale 1.0.0 template-default value exactly, so the
        // install gate can never refuse something the load gate would have let load fine.
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1, minHostVersion: "1.0.0"),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor, hostVersion: new Version(0, 13, 0));

        Assert.True(result.IsSuccess);
    }

    // AC-181 review: an honest sub-1.0 minHostVersion is a real, current requirement (21+ manifests already carry
    // one) and must be enforced against a 0.x host exactly as against a 1.x one — only the stale 1.0.0 template
    // default is exempt pre-1.0, not every sub-1.0 host comparison.
    [Fact]
    public async Task InstallFromZipAsync_HonestSubOnePointZeroMinHostVersion_IsEnforced_EvenOnASubOnePointZeroHost()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1, minHostVersion: "0.14.0"),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor, hostVersion: new Version(0, 13, 0));

        Assert.False(result.IsSuccess);
        Assert.Contains("0.14.0", result.Error);
    }

    [Fact]
    public async Task InstallFromZipAsync_UnparsableMinHostVersion_NotRejectedOverIt()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1, minHostVersion: "not-a-version"),
            ["Plugin.dll"] = "MZ",
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor, hostVersion: new Version(1, 5, 0));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InstallFromZipAsync_MissingManifest_Rejected()
    {
        var zip = _CreateZip(new() { ["Plugin.dll"] = "MZ" });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.Contains("plugin.json", result.Error);
    }

    [Fact]
    public async Task InstallFromZipAsync_MissingEntryAssembly_Rejected()
    {
        var zip = _CreateZip(new() { ["plugin.json"] = _Manifest("x", "X", "Plugin.dll", abstractionsVersion: 1) });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.Contains("Plugin.dll", result.Error);
    }

    // AC-1159: entryAssembly is manifest data, not a zip entry name -- PluginInstallPath's zip-slip guard
    // (above) never sees it. Left unchecked, `Path.Combine(stagingDir, "../approved-plugin/entry.dll")`
    // walks out of staging and hashes/installs over an already-approved sibling's real assembly instead.
    [Fact]
    public async Task InstallFromZipAsync_EntryAssemblyEscapesViaDotDot_Rejected()
    {
        await _installer.InstallFromZipAsync(_PluginZip("approved-plugin", dll: "MZ-approved"), HostMajor);
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("victim", "Victim", "../approved-plugin/Plugin.dll", abstractionsVersion: 1),
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "victim")));
    }

    [Fact]
    public async Task InstallFromZipAsync_EntryAssemblyIsRooted_Rejected()
    {
        var outside = Path.Combine(_tempDir, "elsewhere.dll");
        await File.WriteAllTextAsync(outside, "elsewhere-bytes");
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("victim", "Victim", outside.Replace('\\', '/'), abstractionsVersion: 1),
        });

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "victim")));
    }

    [Fact]
    public async Task InstallFromZipAsync_ZipSlipEntry_Rejected()
    {
        // A crafted entry escaping the destination must be refused before anything lands on disk.
        var zip = Path.Combine(_tempDir, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("pwned");
        }

        var result = await _installer.InstallFromZipAsync(zip, HostMajor);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(_tempDir, "escape.txt")));
    }

    [Fact]
    public async Task MarkForRemovalAsync_ThenSweep_DeletesFolder()
    {
        var zip = _CreateZip(new()
        {
            ["plugin.json"] = _Manifest("gone", "Gone", "Plugin.dll", abstractionsVersion: 1),
            ["Plugin.dll"] = "MZ",
        });
        await _installer.InstallFromZipAsync(zip, HostMajor);

        await _installer.MarkForRemovalAsync("gone");
        await _installer.SweepRemovalsAsync();

        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "gone")));
    }

    [Fact]
    public async Task InstallFromZipAsync_UpdateOverExistingInstall_StagesPendingWithoutReplacingCurrent()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);

        var result = await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);

        Assert.True(result.IsSuccess);
        Assert.Equal("acme", result.FolderId);
        // The live install is untouched (its assembly may be loaded/locked); the new version waits in staging.
        Assert.Equal("MZ-v1", (await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll"))));
        Assert.Equal("MZ-v2", (await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, ".pending-updates", "acme", "Plugin.dll"))));
    }

    [Fact]
    public async Task InstallFromZipAsync_UpdateResult_CarriesTheNewBytesSha256_NotTheOld()
    {
        // The update result must hash the NEW (staged) bytes, so the manager can pin that hash and keep the
        // plugin enabled after the restart swap — pinning the still-live old bytes' hash was the disable bug.
        var v1 = await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        var v2 = await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);

        Assert.False(string.IsNullOrEmpty(v1.Sha256));
        Assert.False(string.IsNullOrEmpty(v2.Sha256));
        Assert.NotEqual(v1.Sha256, v2.Sha256);

        // v1 is a fresh install (not staged); v2 is an update over it (staged to .pending-updates).
        Assert.False(v1.Staged);
        Assert.True(v2.Staged);
    }

    [Fact]
    public async Task SweepPendingUpdatesAsync_AppliesStagedUpdate_ReplacingTheOldFolder()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);

        await _installer.SweepPendingUpdatesAsync();

        Assert.Equal("MZ-v2", (await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll"))));
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, ".pending-updates")));
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
