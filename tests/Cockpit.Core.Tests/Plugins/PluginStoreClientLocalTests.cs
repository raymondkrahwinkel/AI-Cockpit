using System.Text;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The local-folder store path of <see cref="PluginStoreClient"/> (AC-7): index and zips read from disk, the published checksum still verified, and a path that reaches outside the store folder refused.</summary>
public class PluginStoreClientLocalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PluginStoreClient _client = new();

    public PluginStoreClientLocalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-store-client-local-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task FetchIndexAsync_LocalFolder_ReadsAndParsesTheIndex()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "index.json"), """{ "name": "My store", "plugins": [] }""");

        var result = await _client.FetchIndexAsync(PluginStoreConfig.Local(_tempDir));

        Assert.True(result.IsSuccess);
        Assert.Equal("My store", result.Index!.Name);
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_ReturnsTheBytesWhenChecksumMatches()
    {
        var bytes = Encoding.UTF8.GetBytes("a plugin zip's bytes");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), bytes);
        var sha = PluginHash.Compute(bytes);

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", sha);

        Assert.True(result.IsSuccess);
        Assert.Equal(bytes, File.ReadAllBytes(result.ZipPath!));
        Assert.Null(result.Warning);
        _TryDelete(result.ZipPath);
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_WarnsButAllowsWhenNoChecksumPublished()
    {
        // An index without a per-artifact checksum still installs (many simple stores publish none), but the
        // download's integrity could not be verified, so the operator is told (AC-46).
        var bytes = Encoding.UTF8.GetBytes("a plugin zip's bytes");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), bytes);

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", expectedSha256: null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ZipPath);
        Assert.Contains("checksum", result.Warning);
        _TryDelete(result.ZipPath);
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_RejectsAChecksumMismatch()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), Encoding.UTF8.GetBytes("the real bytes"));

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", "0000deadbeef");

        Assert.False(result.IsSuccess);
        Assert.Null(result.ZipPath);
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_RefusesAPathOutsideTheStore()
    {
        // A malicious index.json must not be able to read a file outside its own folder.
        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "../../etc/passwd", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("outside", result.Error);
    }

    [Fact]
    public async Task DownloadTemplateAsync_LocalFolder_ReturnsTheFlowJson()
    {
        var json = """{ "steps": [] }""";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "flow.json"), json);

        var result = await _client.DownloadTemplateAsync(PluginStoreConfig.Local(_tempDir), "flow.json", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(json, result.Json);
        // No checksum was supplied, so the template download carries the same unverified advisory (AC-46).
        Assert.Contains("checksum", result.Warning);
    }

    private static void _TryDelete(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
