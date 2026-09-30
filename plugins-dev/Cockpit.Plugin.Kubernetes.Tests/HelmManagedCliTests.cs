using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The Helm managed-CLI descriptor (AC-1061 phase 3, AC 6/7/8): version parsing, the two-train version selection, the
// get.helm.sh URL/entry mapping and the .sha256sum parsing. Fixtures mirror the real
// api.github.com/repos/helm/helm/releases shape.
public class HelmManagedCliTests
{
    [Fact]
    public void ResolveLatestVersion_SkipsDraftsAndPrereleases()
    {
        const string releases = """
            [
              { "tag_name": "v4.3.0", "draft": true,  "prerelease": false, "published_at": "2026-08-20T00:00:00Z" },
              { "tag_name": "v4.2.9", "draft": false, "prerelease": true,  "published_at": "2026-08-19T00:00:00Z" },
              { "tag_name": "v4.2.4", "draft": false, "prerelease": false, "published_at": "2026-08-13T00:00:00Z" }
            ]
            """;

        Assert.Equal("4.2.4", HelmManagedCli.ResolveLatestVersion(releases));
    }

    [Fact]
    public void ParseChecksum_FilenameMismatch_Throws()
    {
        const string sha256Sum = "aabbccdd112233  helm-v4.2.4-darwin-arm64.tar.gz\n";

        Assert.Throws<InvalidOperationException>(() => HelmManagedCli.ParseChecksum(sha256Sum, "helm-v4.2.4-linux-amd64.tar.gz"));
    }

    // The two plans below are also where AssetName and EntryName are measured: the Url each asserts carries the
    // asset name whole, and ExecutableEntryName is the entry name itself, on both platforms.
    [Fact]
    public async Task BuildPlanAsync_Linux_FetchesTheChecksumAndBuildsATarGzPlan()
    {
        var handler = new _StubHandler(url =>
            url.EndsWith(".sha256sum", StringComparison.Ordinal)
                ? "deadbeef01  helm-v4.2.4-linux-amd64.tar.gz\n"
                : throw new InvalidOperationException($"unexpected request to '{url}'"));
        using var http = new HttpClient(handler);
        var platform = new ManagedCliPlatform("linux", "x64", IsMusl: false);

        var plan = await HelmManagedCli.BuildPlanAsync(http, platform, "4.2.4", CancellationToken.None);

        Assert.Equal("https://get.helm.sh/helm-v4.2.4-linux-amd64.tar.gz", plan.Url);
        Assert.Equal("deadbeef01", plan.ExpectedSha256);
        Assert.Equal(ManagedCliArchiveFormat.TarGz, plan.ArchiveFormat);
        Assert.Equal("linux-amd64/helm", plan.ExecutableEntryName);
        Assert.Equal("helm", plan.ExecutableFileName);
        Assert.True(plan.NeedsExecutableBit);
    }

    private sealed class _StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.ToString())),
            });
    }
}
