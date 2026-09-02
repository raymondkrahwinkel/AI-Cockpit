using System.Formats.Tar;
using System.IO.Compression;
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
    public async Task EnsureInstalled_RawBinary_Downloads_Verifies_Places_AndMarksExecutable()
    {
        var payload = "#!/bin/sh\necho hi\n"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(payload));
        var service = _Service(handler);
        service.Register(_Descriptor("acme", "1.2.3", _RawPlan(payload)));

        var result = await service.EnsureInstalledAsync("acme");

        Assert.True(result.Success);
        Assert.Equal("1.2.3", result.Version);
        var expected = Path.Combine(_root, "cli", "acme", "1.2.3", "acme");
        Assert.Equal(expected, result.ExecutablePath);
        Assert.True(File.Exists(expected));
        Assert.Equal(payload, await File.ReadAllBytesAsync(expected));
        // The half-built ".download" staging dir must be gone once the swap completed.
        Assert.False(Directory.Exists(Path.Combine(_root, "cli", "acme", "1.2.3.download")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(expected).HasFlag(UnixFileMode.UserExecute));
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

    [Fact]
    public async Task EnsureInstalled_AlreadyInstalled_IsCacheHit_AndDoesNotDownload()
    {
        var payload = "already here"u8.ToArray();
        // A handler that would throw if hit — proving the cache-hit path never downloads.
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not download"));
        var service = _Service(handler);
        service.Register(_Descriptor("acme", "2.0.0", _RawPlan(payload)));

        var versionDir = Path.Combine(_root, "cli", "acme", "2.0.0");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllBytesAsync(Path.Combine(versionDir, "acme"), payload);

        var result = await service.EnsureInstalledAsync("acme");

        Assert.True(result.Success);
        Assert.Equal("2.0.0", result.Version);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EnsureInstalled_NetworkFailure_ReturnsFailure_RatherThanThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        var service = _Service(handler);
        service.Register(_Descriptor("acme", "1.0.0", _RawPlan("x"u8.ToArray())));

        var result = await service.EnsureInstalledAsync("acme");

        Assert.False(result.Success);
        Assert.Contains("acme", result.Error);
    }

    [Theory]
    [InlineData("../../../etc/cron.d/x")] // path traversal
    [InlineData("..")]
    [InlineData("1.2.3-alpha.1")]         // non-numeric: install would be invisible to resolution
    [InlineData("not-a-version")]
    public async Task EnsureInstalled_RejectsUnsafeOrNonNumericVersion_BeforeDownloading(string version)
    {
        // A handler that throws if hit — the version is refused before any bytes are fetched.
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not download"));
        var service = _Service(handler);
        service.Register(_Descriptor("acme", version, _RawPlan("x"u8.ToArray())));

        var result = await service.EnsureInstalledAsync("acme");

        Assert.False(result.Success);
        Assert.Contains("version", result.Error);
        Assert.Equal(0, handler.CallCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "cli", "acme")));
    }

    [Fact]
    public async Task EnsureInstalled_NoDescriptor_Fails()
    {
        var result = await _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException())).EnsureInstalledAsync("unknown");

        Assert.False(result.Success);
        Assert.Contains("unknown", result.Error);
    }

    [Fact]
    public async Task EnsureInstalled_TarGz_ExtractsTheNamedEntry()
    {
        var binary = "codex-native-bytes"u8.ToArray();
        var archive = _TarGz("codex-x86_64-unknown-linux-musl/codex", binary);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive));
        var service = _Service(handler);

        var plan = new ManagedCliDownloadPlan
        {
            Url = "https://example.test/codex.tar.gz",
            ExpectedSha256 = PluginHash.Compute(archive),
            ExecutableFileName = "codex",
            ArchiveFormat = ManagedCliArchiveFormat.TarGz,
            ExecutableEntryName = "codex",
            NeedsExecutableBit = true,
        };
        service.Register(_Descriptor("codex", "0.9.0", plan));

        var result = await service.EnsureInstalledAsync("codex");

        Assert.True(result.Success);
        var expected = Path.Combine(_root, "cli", "codex", "0.9.0", "codex");
        Assert.True(File.Exists(expected));
        Assert.Equal(binary, await File.ReadAllBytesAsync(expected));
    }

    [Fact]
    public async Task EnsureInstalled_Zip_ExtractsTheNamedEntry()
    {
        var binary = "helm-native-bytes"u8.ToArray();
        var archive = _Zip("windows-amd64/helm.exe", binary);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive));
        var service = _Service(handler);

        var plan = new ManagedCliDownloadPlan
        {
            Url = "https://example.test/helm.zip",
            ExpectedSha256 = PluginHash.Compute(archive),
            ExecutableFileName = "helm.exe",
            ArchiveFormat = ManagedCliArchiveFormat.Zip,
            ExecutableEntryName = "windows-amd64/helm.exe",
            NeedsExecutableBit = false,
        };
        service.Register(_Descriptor("helm", "4.2.4", plan));

        var result = await service.EnsureInstalledAsync("helm");

        Assert.True(result.Success);
        var expected = Path.Combine(_root, "cli", "helm", "4.2.4", "helm.exe");
        Assert.True(File.Exists(expected));
        Assert.Equal(binary, await File.ReadAllBytesAsync(expected));
    }

    [Fact]
    public async Task EnsureInstalled_WithAdditionalArtifacts_DownloadsVerifiesAndPlacesAll()
    {
        // AC-1107: a recipe that promises a sibling binary (Codex's code-mode-host) must actually place it, not
        // just the primary executable — this is the test the acceptance criteria ask for.
        var primary = "primary bytes"u8.ToArray();
        var sibling = "sibling bytes"u8.ToArray();
        var handler = new StubHttpMessageHandler(request => StubHttpMessageHandler.Bytes(
            request.RequestUri!.AbsoluteUri.EndsWith("sibling", StringComparison.Ordinal) ? sibling : primary));
        var service = _Service(handler);
        var plan = _RawPlan(primary) with
        {
            AdditionalArtifacts =
            [
                new ManagedCliDownloadArtifact
                {
                    Url = "https://example.test/sibling",
                    ExpectedSha256 = PluginHash.Compute(sibling),
                    FileName = "acme-helper",
                    NeedsExecutableBit = true,
                },
            ],
        };
        service.Register(_Descriptor("acme", "1.0.0", plan));

        var result = await service.EnsureInstalledAsync("acme");

        Assert.True(result.Success);
        var versionDir = Path.Combine(_root, "cli", "acme", "1.0.0");
        Assert.Equal(primary, await File.ReadAllBytesAsync(Path.Combine(versionDir, "acme")));
        Assert.Equal(sibling, await File.ReadAllBytesAsync(Path.Combine(versionDir, "acme-helper")));
    }

    [Fact]
    public async Task EnsureInstalled_ExistingVersionMissingAdditionalArtifact_RepairsInPlace_WithoutRedownloadingPrimary()
    {
        // The AC-1107 repair case: a version directory installed before the recipe grew a sibling binary. The
        // primary is already on disk and correct — only the missing file should be fetched.
        var sibling = "sibling bytes"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(sibling));
        var service = _Service(handler);
        var plan = _RawPlan("primary bytes"u8.ToArray()) with
        {
            AdditionalArtifacts =
            [
                new ManagedCliDownloadArtifact
                {
                    Url = "https://example.test/sibling",
                    ExpectedSha256 = PluginHash.Compute(sibling),
                    FileName = "acme-helper",
                },
            ],
        };
        service.Register(_Descriptor("acme", "1.0.0", plan));
        _PlaceInstalled("acme", "1.0.0"); // only the primary "acme" file, as an older recipe would have left it

        var result = await service.EnsureInstalledAsync("acme");

        Assert.True(result.Success);
        Assert.Equal(1, handler.CallCount); // only the missing sibling was fetched
        var versionDir = Path.Combine(_root, "cli", "acme", "1.0.0");
        Assert.Equal(sibling, await File.ReadAllBytesAsync(Path.Combine(versionDir, "acme-helper")));
    }

    [Fact]
    public void ResolveInstalledPath_PicksNewestVersion_ByVersionOrder()
    {
        _PlaceInstalled("acme", "1.2.0");
        _PlaceInstalled("acme", "1.10.0"); // string-sorts below 1.2.0; version order must win
        _PlaceInstalled("acme", "1.3.0");

        Assert.Equal(Path.Combine(_root, "cli", "acme", "1.10.0", "acme"), _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException()))
            .ResolveInstalledPath("acme"));
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

    [Fact]
    public async Task GetStatus_ReportsInstalledAndLatestVersion()
    {
        var service = _Service(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes("x"u8.ToArray())));
        service.Register(_Descriptor("acme", "2.0.0", _RawPlan("x"u8.ToArray()))); // descriptor's latest = 2.0.0
        _PlaceInstalled("acme", "1.0.0");

        var status = await service.GetStatusAsync("acme");

        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Equal("2.0.0", status.LatestVersion);
    }

    [Fact]
    public async Task GetStatus_NotInstalled_ReportsNullInstalled_ButStillLatest()
    {
        var service = _Service(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes("x"u8.ToArray())));
        service.Register(_Descriptor("acme", "2.0.0", _RawPlan("x"u8.ToArray())));

        var status = await service.GetStatusAsync("acme");

        Assert.Null(status.InstalledVersion);
        Assert.Equal("2.0.0", status.LatestVersion);
    }

    [Fact]
    public async Task GetStatus_ChannelUnreachable_ReportsInstalledButNullLatest()
    {
        // No descriptor registered → the latest cannot be determined; the installed copy is still reported, so the UI
        // falls back to a plain "Update" rather than a false "up to date".
        var service = _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException()));
        _PlaceInstalled("acme", "1.0.0");

        var status = await service.GetStatusAsync("acme");

        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Null(status.LatestVersion);
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

    private static byte[] _TarGz(string entryName, byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(content),
            };
            tar.WriteEntry(entry);
        }

        return output.ToArray();
    }

    private static byte[] _Zip(string entryName, byte[] content)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        return output.ToArray();
    }
}
