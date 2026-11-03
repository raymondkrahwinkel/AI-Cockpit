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
    public async Task DownloadZipAsync_LocalFolder_RejectsAChecksumMismatch_AndAPathOutsideTheStore()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "plugin.zip"), Encoding.UTF8.GetBytes("the real bytes"));

        var result = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "plugin.zip", "0000deadbeef");

        Assert.False(result.IsSuccess);
        Assert.Null(result.ZipPath);

        // A malicious index.json must not be able to read a file outside its own folder.
        var outside = await _client.DownloadZipAsync(PluginStoreConfig.Local(_tempDir), "../../etc/passwd", null);

        Assert.False(outside.IsSuccess);
        Assert.Contains("outside", outside.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
