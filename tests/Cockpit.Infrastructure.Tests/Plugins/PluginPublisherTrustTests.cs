using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;
using Org.BouncyCastle.Crypto.Parameters;

namespace Cockpit.Infrastructure.Tests.Plugins;

public sealed class PluginPublisherTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-publisher-tests", Guid.NewGuid().ToString("N"));
    private readonly IPluginStoreClient _client = Substitute.For<IPluginStoreClient>();
    private readonly PluginTrustStore _trust;
    private readonly PluginInstaller _installer;
    private readonly PluginRegistrationStore _registrations;
    private readonly PluginProvisioningService _service;
    private string _lastZip = "";

    public PluginPublisherTrustTests()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "cockpit.json");
        _trust = new PluginTrustStore(config);
        _registrations = new PluginRegistrationStore(config);
        _installer = new PluginInstaller(Path.Combine(_root, "plugins"));
        _service = new PluginProvisioningService(_client, _installer, new PluginSignatureVerifier(), _trust);
    }

    [Theory]
    [InlineData("other", "1.0.0", null, false)]
    [InlineData("acme", "2.0.0", null, false)]
    [InlineData("acme", "1.0.0", "plugin.json", false)]
    [InlineData("acme", "1.0.0", "PLUGIN.JSON", false)]
    [InlineData("acme", "1.0.0", "plugin.json.", false)]
    [InlineData("acme", "1.0.0", "x/../plugin.json", false)]
    [InlineData("acme", "1.0.0", null, true)]
    public async Task SignedZipThatDoesNotBind_CannotWriteAPluginOrChangePins(string manifestId, string manifestVersion, string? alias, bool tamper)
    {
        await _registrations.SaveAsync("acme", new PluginRegistration(true, "previous-pin"));
        var request = _Package("1.0.0", 1, manifestId, manifestVersion, alias, tamper);

        var result = await _service.InstallAsync(request, 3, keyConsent: _AcceptAsync);

        Assert.Equal(PluginProvisionOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_root, "plugins")));
        Assert.False(File.Exists(_lastZip));
        Assert.Null(await _trust.GetAsync("acme"));
        Assert.Equal("previous-pin", (await _registrations.LoadAllAsync())["acme"].PinnedSha256);
    }

    [Fact]
    public async Task PublisherTrust_RequiresFirstConsentAndNeverSoftensAKeyChangeBecauseTheOldKeyWasRevoked()
    {
        var first = await _service.InstallAsync(_Package("1.0.0", 1), 3);
        Assert.Equal(PluginProvisionOutcome.KeyNotConfirmed, first.Outcome);
        Assert.Null(await _trust.GetAsync("acme"));

        Assert.True((await _service.InstallAsync(_Package("1.0.0", 1), 3, keyConsent: _AcceptAsync)).IsSuccess);
        var original = await _trust.GetAsync("acme");
        Assert.NotNull(original);
        Assert.True((await _service.InstallAsync(_Package("1.1.0", 1), 3)).IsSuccess);
        var installed = await File.ReadAllTextAsync(Path.Combine(_root, "plugins", "acme", "plugin.json"));
        var changed = _Package("1.2.0", 2) with { RevokedKeys = [new(original.KeyFingerprint, "old", null)] };

        var denied = await _service.InstallAsync(changed, 3);

        Assert.Equal(PluginProvisionOutcome.KeyNotConfirmed, denied.Outcome);
        Assert.Equal(original, await _trust.GetAsync("acme"));
        Assert.Equal(installed, await File.ReadAllTextAsync(Path.Combine(_root, "plugins", "acme", "plugin.json")));
        var decisions = new List<PluginTrustDecision>();
        var accepted = await _service.InstallAsync(_Package("1.2.0", 2) with { RevokedKeys = changed.RevokedKeys }, 3,
            keyConsent: (consent, _) => { decisions.Add(consent.Decision); return Task.FromResult(true); });
        Assert.True(accepted.IsSuccess);
        Assert.Equal(new[] { PluginTrustDecision.KeyChanged }, decisions);
        Assert.NotEqual(original, await _trust.GetAsync("acme"));
        var current = await _trust.GetAsync("acme");
        var differentSource = _Package("1.3.0", 2);
        differentSource = differentSource with { Store = differentSource.Store with { Signed = (differentSource.Store.Signed ?? throw new InvalidOperationException("Missing fixture source")) with { SourceKey = "depot:other.example/acme" } } };
        Assert.Equal(PluginProvisionOutcome.KeyNotConfirmed, (await _service.InstallAsync(differentSource, 3)).Outcome);
        Assert.Equal(current, await _trust.GetAsync("acme"));
        var unsigned = _Package("1.3.0", 2) with { Store = PluginStoreConfig.Local(_root) };
        _client.DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<PluginDownloadProgress>?>())
            .Returns(new PluginStoreDownloadResult(true, null, _lastZip));
        var switched = await _service.InstallAsync(unsigned, 3, keyConsent: (consent, _) =>
        {
            decisions.Add(consent.Decision);
            return Task.FromResult(true);
        });
        Assert.True(switched.IsSuccess);
        Assert.Equal(PluginTrustDecision.SourceChanged, decisions.Last());
        Assert.Null(await _trust.GetAsync("acme"));
    }

    [Fact]
    public async Task RemovingAndReinstalling_APluginKeepsItsPublisherRule_AndARevocationStillBlocksIt()
    {
        Assert.True((await _service.InstallAsync(_Package("1.0.0", 1), 3, keyConsent: _AcceptAsync)).IsSuccess);
        var original = await _trust.GetAsync("acme");
        Assert.NotNull(original);
        await _registrations.RemoveAsync("acme");
        await _installer.MarkForRemovalAsync("acme");
        await _installer.SweepRemovalsAsync();
        Assert.Equal(original, await new PluginTrustStore(Path.Combine(_root, "cockpit.json")).GetAsync("acme"));
        Assert.True((await _service.InstallAsync(_Package("1.0.0", 1), 3)).IsSuccess);
        var revoked = _Package("1.1.0", 1) with { RevokedKeys = [new(original.KeyFingerprint, null, null)] };
        Assert.Equal(PluginProvisionOutcome.Failed, (await _service.InstallAsync(revoked, 3, keyConsent: _AcceptAsync)).Outcome);
        Assert.Equal(original, await _trust.GetAsync("acme"));
    }

    [Theory]
    [InlineData("acme.tools", "1.0.0")]
    [InlineData("acme", "1.0.0-beta")]
    public async Task InvalidSignedIdentity_IsRejectedBeforeDownloading(string id, string version)
    {
        var request = _Package("1.0.0", 1);
        request = request with { Id = id, Version = request.Version with { Version = version } };
        _client.ClearReceivedCalls();

        var result = await _service.InstallAsync(request, 3, keyConsent: _AcceptAsync);

        Assert.Equal(PluginProvisionOutcome.Failed, result.Outcome);
        Assert.Empty(_client.ReceivedCalls());
        Assert.True(File.Exists(_lastZip));
        Assert.False(Directory.Exists(Path.Combine(_root, "plugins")));
    }

    [Fact]
    public async Task RestoreTrust_IsAFallbackAndCannotOverrideALocalDecision()
    {
        var request = _Package("1.0.0", 1);
        var seed = Enumerable.Repeat((byte)1, 32).ToArray();
        var fingerprint = SshSig.FingerprintOf(new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded());
        var archived = new PluginTrustEntry("depot:depot.example/acme", fingerprint);

        var restored = await _service.InstallAsync(request with { RestoredTrust = archived }, 3);

        Assert.True(restored.IsSuccess);
        Assert.Equal(archived, await _trust.GetAsync("acme"));
        var local = new PluginTrustEntry(archived.Source, "newer-local-key");
        await _trust.SaveAsync("acme", local);
        var rejected = await _service.InstallAsync(_Package("1.1.0", 1) with { RestoredTrust = archived }, 3);
        Assert.Equal(PluginProvisionOutcome.KeyNotConfirmed, rejected.Outcome);
        Assert.Equal(local, await _trust.GetAsync("acme"));
    }

    [Fact]
    public async Task ExtractedIdentity_IsCheckedAgainBeforeAnInstallerMovesAnyFolder()
    {
        _Package("1.0.0", 1);
        var result = await _installer.InstallFromZipAsync(_lastZip, 3, expectedId: "other", expectedVersion: "1.0.0");

        Assert.False(result.IsSuccess);
        var wrongVersion = await _installer.InstallFromZipAsync(_lastZip, 3, expectedId: "acme", expectedVersion: "2.0.0");
        Assert.False(wrongVersion.IsSuccess);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "plugins")));
        Assert.Null(await _trust.GetAsync("acme"));
    }

    [Fact]
    public void APluginsOwnBouncyCastle_StaysInItsLoadContext()
    {
        var host = typeof(Ed25519PrivateKeyParameters).Assembly;
        var folder = Path.Combine(_root, "own-dependency");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(host.Location));
        File.Copy(host.Location, copy);
        var context = new PluginLoadContext(copy);

        var plugin = context.LoadOnce(copy);

        Assert.NotSame(host, plugin);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(host));
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(plugin));
        Assert.Same(plugin, context.LoadFromAssemblyName(host.GetName()));
    }

    private PluginProvisionRequest _Package(string version, byte seedByte, string manifestId = "acme", string? manifestVersion = null, string? alias = null, bool tamper = false)
    {
        _lastZip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(_lastZip, ZipArchiveMode.Create))
        {
            var manifest = JsonSerializer.Serialize(new { id = manifestId, name = "Acme", version = manifestVersion ?? version, entryAssembly = "Plugin.dll", abstractionsVersion = 3 });
            using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open())) { writer.Write(manifest); }
            if (alias is not null)
            {
                using var writer = new StreamWriter(archive.CreateEntry(alias).Open());
                writer.Write(manifest);
            }
            using var dll = new StreamWriter(archive.CreateEntry("Plugin.dll").Open());
            dll.Write("test bytes " + version);
        }
        var sha = PluginHash.Compute(File.ReadAllBytes(_lastZip));
        var seed = Enumerable.Repeat(seedByte, 32).ToArray();
        var key = new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();
        var signature = SshSig.Sign(seed, key, SshSig.Statement("acme", "acme", version, "any", sha, "", ""));
        if (tamper)
        {
            using var stream = new FileStream(_lastZip, FileMode.Append);
            stream.WriteByte(1);
        }
        _client.DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<PluginStoreVersion>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IProgress<PluginDownloadProgress>?>())
            .Returns(new PluginStoreDownloadResult(true, null, _lastZip));
        var store = PluginStoreConfig.Remote("https://depot.example/cockpit/stores/acme/index.json") with
        {
            Signed = new PluginStoreSignedSource("depot:depot.example/acme", "acme", _ => ValueTask.FromResult<string?>(null)),
        };
        return new PluginProvisionRequest("acme", "Acme", store, new PluginStoreVersion(version, "ignored.zip", 3, null, sha, null, Signature: signature, KeyFingerprint: "untrusted-feed-value"));
    }

    private static Task<bool> _AcceptAsync(PluginKeyConsentRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

    public void Dispose()
    {
        // Windows keeps the dependency copy locked until this test process ends.
        foreach (var file in Directory.EnumerateFiles(_root, "*.zip")) { File.Delete(file); }
        foreach (var folder in Directory.EnumerateDirectories(_root).Where(path => Path.GetFileName(path) != "own-dependency")) { Directory.Delete(folder, true); }
        foreach (var file in Directory.EnumerateFiles(_root)) { File.Delete(file); }
    }
}
