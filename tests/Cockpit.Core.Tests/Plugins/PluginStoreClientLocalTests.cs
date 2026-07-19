using System.Text;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

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

        result.IsSuccess.Should().BeTrue();
        result.Index!.Name.Should().Be("My store");
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_ReturnsTheBytesWhenChecksumMatches()
    {
        var bytes = Encoding.UTF8.GetBytes("a plugin zip's bytes");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), bytes);
        var sha = PluginHash.Compute(bytes);

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", sha);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllBytes(result.ZipPath!).Should().Equal(bytes);
        result.Warning.Should().BeNull("a verified checksum carries no advisory");
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

        result.IsSuccess.Should().BeTrue();
        result.ZipPath.Should().NotBeNull();
        result.Warning.Should().Contain("checksum");
        _TryDelete(result.ZipPath);
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_RejectsAChecksumMismatch()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), Encoding.UTF8.GetBytes("the real bytes"));

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", "0000deadbeef");

        result.IsSuccess.Should().BeFalse();
        result.ZipPath.Should().BeNull();
    }

    [Fact]
    public async Task DownloadZipAsync_LocalFolder_RefusesAPathOutsideTheStore()
    {
        // A malicious index.json must not be able to read a file outside its own folder.
        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "../../etc/passwd", null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("outside");
    }

    [Fact]
    public async Task DownloadTemplateAsync_LocalFolder_ReturnsTheFlowJson()
    {
        var json = """{ "steps": [] }""";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "flow.json"), json);

        var result = await _client.DownloadTemplateAsync(PluginStoreConfig.Local(_tempDir), "flow.json", null);

        result.IsSuccess.Should().BeTrue();
        result.Json.Should().Be(json);
        // No checksum was supplied, so the template download carries the same unverified advisory (AC-46).
        result.Warning.Should().Contain("checksum");
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
