using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Infrastructure.Tests.ManagedCli;

/// <summary>
/// The generic managed-CLI installer (AC-20): download → verify SHA-256 → unpack → place atomically, and resolve the
/// newest installed version. The provider-specific descriptor is faked here (canned version + plan), so these assert
/// the host-side machinery every provider shares, not any Claude/Codex specifics.
/// </summary>
public sealed class ManagedCliServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cockpit-mcli-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInstalled_ChecksumMismatch_IsRejected_AndInstallsNothing()
    {
        var payload = "the real bytes"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(payload));
        var service = _Service(handler);
        // A plan whose expected hash is for different content — the download must be refused.
        var plan = _RawPlan(payload) with { ExpectedSha256 = PluginHash.Compute("something else entirely"u8.ToArray()) };
        service.Register(_Descriptor("acme", "1.0.0", plan));

        var result = await service.EnsureInstalledAsync("acme");

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "cli", "acme", "1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(_root, "cli", "acme", "1.0.0.download")));
        Assert.Null(service.ResolveInstalledPath("acme"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task PathBuildingMethods_RejectAnUnsafeCliName(string cliName)
    {
        // A cli name becomes a path segment; a separator or dot-segment must never resolve, remove or install anywhere.
        _PlaceInstalled("acme", "1.0.0"); // a real install exists, but not under the unsafe name
        var service = _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not download")));
        service.Register(_Descriptor(cliName, "1.0.0", _RawPlan("x"u8.ToArray())));

        Assert.Null(service.ResolveInstalledPath(cliName));
        Assert.False(service.RemoveInstalled(cliName));
        Assert.False((await service.EnsureInstalledAsync(cliName)).Success);
    }

    // The internal ctor takes the cli root directly (in production that is <StateRoot>/cli); mirror that layout so
    // the asserted paths read <root>/cli/<name>/<version>/<exe>.
    private ManagedCliService _Service(StubHttpMessageHandler handler) =>
        new(Path.Combine(_root, "cli"), new HttpClient(handler), logger: null);

    private void _PlaceInstalled(string cliName, string version)
    {
        var dir = Path.Combine(_root, "cli", cliName, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, cliName), "x");
    }

    private static ManagedCliDownloadPlan _RawPlan(byte[] payload) => new()
    {
        Url = "https://example.test/acme",
        ExpectedSha256 = PluginHash.Compute(payload),
        ExecutableFileName = "acme",
        NeedsExecutableBit = true,
    };

    private static ManagedCliDescriptor _Descriptor(string cliName, string version, ManagedCliDownloadPlan plan) => new()
    {
        CliName = cliName,
        ResolveLatestVersionAsync = (_, _) => Task.FromResult(version),
        BuildDownloadPlanAsync = (_, _, _, _) => Task.FromResult(plan),
    };

}
