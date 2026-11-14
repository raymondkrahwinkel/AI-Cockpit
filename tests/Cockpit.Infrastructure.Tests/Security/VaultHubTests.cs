using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Vault;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Security;

// Category A (AC-2336, AC-2346): the vault's hard boundaries. If one breaks silently, a secret lands on disk or at the model provider.
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

    // Also category B (AC-2364): an unavailable vault refuses use but never deletes the stored connection.
    [Fact]
    public async Task WithEncryptionOff_TheHubRefusesToStore_AndNeverDeletesAStoredConnection()
    {
        var holder = new SecretKeyHolder();
        await File.WriteAllTextAsync(ConfigPath, "{ \"Profiles\": [] }");
        var protection = new SecretProtectionService(ConfigPath, holder);
        var hub = new VaultHub(protection, ConfigPath, holder, NullLogger<VaultHub>.Instance);
        var context = hub.Register("plugin-a", new Provider());

        Assert.NotNull(context);
        Assert.Equal(VaultAvailability.EncryptionOff, context.Availability);
        Assert.False(context.TryStoreCredential(Canary));
        Assert.Null(context.ReadCredential());

        var written = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("Vault", written, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, written, StringComparison.Ordinal);

        await protection.EnableAsync("vault-test-password");
        Assert.True(context.TryStoreCredential(Canary));
        var stored = await File.ReadAllTextAsync(ConfigPath);

        // The 2026-11-14 incident: a status read that lands mid-swap reports encryption off while it is on.
        var misread = Substitute.For<ISecretProtectionService>();
        misread.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new SecretProtectionStatus(Enabled: false, Unlocked: false, ShouldWarnUnprotected: false));
        var blind = new VaultHub(misread, ConfigPath, holder, NullLogger<VaultHub>.Instance).Register("plugin-a", new Provider())!;
        Assert.Equal(VaultAvailability.EncryptionOff, blind.Availability);
        Assert.Null(blind.ReadCredential());
        Assert.False(blind.TryStoreCredential("other"));

        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));

        // The race itself: mid-swap, under the write gate, the file is briefly gone; a reader waits instead of reading "off".
        var aside = ConfigPath + ".swapping";
        var gate = await CockpitConfigWriteGate.AcquireAsync(ConfigPath, CancellationToken.None);
        File.Move(ConfigPath, aside);
        var read = Task.Run(context.ReadCredential);
        var answeredMidSwap = await Task.WhenAny(read, Task.Delay(500)) == read;
        File.Move(aside, ConfigPath);
        await gate.DisposeAsync();
        Assert.False(answeredMidSwap);
        Assert.Equal(Canary, await read);

        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));
        await Assert.ThrowsAsync<SecretProtectionException>(() => protection.DisableAsync());
        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task ASessionGrantForOneArgv_DoesNotCoverAnother_AndNeitherAnotherPluginNorTheOutputGetsTheValue()
    {
        var holder = new SecretKeyHolder();
        await File.WriteAllTextAsync(ConfigPath, "{ \"Profiles\": [] }");
        var protection = new SecretProtectionService(ConfigPath, holder);
        await protection.EnableAsync("vault-test-password");
        var hub = new VaultHub(protection, ConfigPath, holder, NullLogger<VaultHub>.Instance);

        // AC-2333 S1: a plugin that takes over a provider id does not inherit the owner's stored credential.
        var owner = new Provider(Canary);
        Assert.True(hub.Register("plugin-a", owner)!.TryStoreCredential(Canary));
        hub.Unregister(owner);
        Assert.Null(hub.Register("plugin-b", new Provider(Canary))!.ReadCredential());
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ConsentDecision(ConsentOutcome.Approved, Remembered: false) { Chosen = new Dictionary<string, IReadOnlyList<string>> { ["scope"] = ["session"] } });
        var session = Substitute.For<ISessionHandle>();
        session.PaneId.Returns("pane-a");
        session.WorkingDirectory.Returns(_directory);
        session.ProjectId.Returns((string?)null);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns(session);
        var tools = new VaultMcpTools(hub, consent, sessions, NullLogger<VaultMcpTools>.Instance);
        string[] printX = OperatingSystem.IsWindows() ? ["cmd", "/c", "echo %X%"] : ["sh", "-c", "echo $X"];
        string[] printXAgain = [.. printX[..^1], printX[^1] + " again"];
        var env = new Dictionary<string, string> { ["X"] = "test://deploy" };

        McpRequestContext.Set("pane-a");
        string[] results =
        [
            await tools.RunAsync(printX, env, "test"),
            await tools.RunAsync(printX, env, "test"),
            await tools.RunAsync(printXAgain, env, "test"),
        ];

        await consent.Received(2).RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
        Assert.All(results, result =>
        {
            Assert.Contains("[vault: Deploy token]", result, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, result, StringComparison.Ordinal);
        });
    }

    private sealed class Provider(string? value = null) : IVaultProvider
    {
        public string Id => "test";

        public string DisplayName => "Test";

        public string ReferenceScheme => "test";

        public Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VaultProviderStatus(VaultProviderState.Connected));

        public Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VaultItem>>([new VaultItem("test://deploy", "Deploy token")]);

        public Task<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            value is null ? throw new InvalidOperationException("The hub must not resolve while encryption is off.") : Task.FromResult(value);
    }
}
