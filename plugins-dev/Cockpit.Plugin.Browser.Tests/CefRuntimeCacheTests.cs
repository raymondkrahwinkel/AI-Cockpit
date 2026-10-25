using System.Net;
using Cockpit.Plugin.Browser.UI.Runtime;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1727, L2b (integrity): the one promise that must hold is that a runtime that is not the pinned one is
// never used. Criteria 2 and 3 (second start, server) are judged in review and by hand, not here.
public class CefRuntimeCacheTests
{
    [Fact]
    public async Task ADownloadWithAWrongChecksum_IsDiscarded_AndLeavesNoRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "cef-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var served = new byte[] { 1, 2, 3, 4 };
            var catalog = new CefRuntimeCatalog(
                "1.0.0", "1.0.0.0", new DateOnly(2026, 10, 5),
                new Dictionary<string, CefRuntimePackage> { ["win-x64"] = new("https://example.invalid/cef.tar.bz2", served.Length, new string('0', 64)) });
            var cache = new CefRuntimeCache(root, new HttpClient(new FixedBytesHandler(served)));

            var result = await cache.EnsureAvailableAsync(catalog, "win-x64", null, CancellationToken.None);

            Assert.Null(result.Directory);
            Assert.Contains("checksum", result.Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "1.0.0")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FixedBytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
