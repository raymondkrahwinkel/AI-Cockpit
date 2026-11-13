using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Vault;
using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Security;

// Category A (AC-2336): the vault's hard condition. If it breaks silently, provider tokens land readable on disk.
public sealed class VaultHubTests : IDisposable
{
    private const string Canary = "vault-canary-7f3c9e";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-vault-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public VaultHubTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WithEncryptionOff_TheHubRefusesToStore_AndWritesNoVaultSection()
    {
        var holder = new SecretKeyHolder();
        await File.WriteAllTextAsync(ConfigPath, "{ \"Profiles\": [] }");
        var hub = new VaultHub(new SecretProtectionService(ConfigPath, holder), ConfigPath, holder, NullLogger<VaultHub>.Instance);
        var context = hub.Register(new Provider());

        Assert.NotNull(context);
        Assert.Equal(VaultAvailability.EncryptionOff, context.Availability);
        Assert.False(context.TryStoreCredential(Canary));
        Assert.Null(context.ReadCredential());

        var written = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("Vault", written, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, written, StringComparison.Ordinal);
    }

    private sealed class Provider : IVaultProvider
    {
        public string Id => "test";

        public string DisplayName => "Test";

        public string ReferenceScheme => "test";

        public Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VaultProviderStatus(VaultProviderState.Connected));

        public Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VaultItem>>([]);

        public Task<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The hub must not resolve while encryption is off.");
    }
}
