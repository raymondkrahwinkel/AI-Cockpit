using System.Net;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// The one gate criterion 3 depends on being a single implementation: own range always allowed, past it only
/// what the whitelist names, checked without touching the network — <see cref="NodeVisibilityPolicy"/>'s test
/// seam swaps in a fixed set of "own" ranges so this proves the range logic itself rather than whatever network
/// the test happens to run on.
/// </summary>
public class NodeVisibilityPolicyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"node-visibility-{Guid.NewGuid():N}.json");

    private static readonly IPNetwork _OwnRange = IPNetwork.Parse("192.168.1.0/24");




    [Fact]
    public async Task AMalformedWhitelistEntry_IsSkipped_NotThrown()
    {
        var store = new NodeEndpointSettingsStore(_path);
        await store.SaveAsync(new NodeEndpointSettings { AllowedDiscoveryRanges = ["not-a-cidr", "203.0.113.0/24"] });
        var policy = new NodeVisibilityPolicy(store, () => [_OwnRange]);

        // A typo in one entry must not take down every check that follows it — the valid sibling entry still works.
        Assert.True(await policy.IsAllowedAsync(IPAddress.Parse("203.0.113.5")));
        Assert.False(await policy.IsAllowedAsync(IPAddress.Parse("198.51.100.5")));
    }


    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
