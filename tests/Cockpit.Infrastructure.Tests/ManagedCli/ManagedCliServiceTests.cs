using System.Formats.Tar;
using System.IO.Compression;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Plugins.Abstractions.ManagedCli;
using FluentAssertions;

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

        result.Success.Should().BeTrue();
        result.Version.Should().Be("1.2.3");
        var expected = Path.Combine(_root, "cli", "acme", "1.2.3", "acme");
        result.ExecutablePath.Should().Be(expected);
        File.Exists(expected).Should().BeTrue();
        (await File.ReadAllBytesAsync(expected)).Should().Equal(payload);
        // The half-built ".download" staging dir must be gone once the swap completed.
        Directory.Exists(Path.Combine(_root, "cli", "acme", "1.2.3.download")).Should().BeFalse();

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(expected).Should().HaveFlag(UnixFileMode.UserExecute);
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

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SHA-256");
        Directory.Exists(Path.Combine(_root, "cli", "acme", "1.0.0")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "cli", "acme", "1.0.0.download")).Should().BeFalse();
        service.ResolveInstalledPath("acme").Should().BeNull();
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

        result.Success.Should().BeTrue();
        result.Version.Should().Be("2.0.0");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureInstalled_NetworkFailure_ReturnsFailure_RatherThanThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        var service = _Service(handler);
        service.Register(_Descriptor("acme", "1.0.0", _RawPlan("x"u8.ToArray())));

        var result = await service.EnsureInstalledAsync("acme");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("acme");
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

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("version");
        handler.CallCount.Should().Be(0);
        Directory.Exists(Path.Combine(_root, "cli", "acme")).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureInstalled_NoDescriptor_Fails()
    {
        var result = await _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException())).EnsureInstalledAsync("unknown");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unknown");
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

        result.Success.Should().BeTrue();
        var expected = Path.Combine(_root, "cli", "codex", "0.9.0", "codex");
        File.Exists(expected).Should().BeTrue();
        (await File.ReadAllBytesAsync(expected)).Should().Equal(binary);
    }

    [Fact]
    public void ResolveInstalledPath_PicksNewestVersion_ByVersionOrder()
    {
        _PlaceInstalled("acme", "1.2.0");
        _PlaceInstalled("acme", "1.10.0"); // string-sorts below 1.2.0; version order must win
        _PlaceInstalled("acme", "1.3.0");

        _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException()))
            .ResolveInstalledPath("acme")
            .Should().Be(Path.Combine(_root, "cli", "acme", "1.10.0", "acme"));
    }

    [Fact]
    public void ResolveInstalledPath_NothingInstalled_ReturnsNull()
    {
        _Service(new StubHttpMessageHandler(_ => throw new InvalidOperationException()))
            .ResolveInstalledPath("acme")
            .Should().BeNull();
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
}
