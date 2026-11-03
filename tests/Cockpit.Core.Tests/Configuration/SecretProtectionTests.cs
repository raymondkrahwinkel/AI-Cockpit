using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// Credential encryption: the settings on disk carry no readable token once the operator turns it on, the
/// migration goes both ways without losing anything, and a wrong password is told apart from a damaged file.
/// </summary>
public class SecretProtectionTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Token = "perm:a-youtrack-token";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-secrets-{Guid.NewGuid():N}");
    private readonly SecretKeyHolder _keyHolder = new();
    private readonly string _configPath;

    public SecretProtectionTests()
    {
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "cockpit.json");
    }

    private SecretProtectionService Service() => new(_configPath, _keyHolder);

    private McpServerStore Store() => new(_configPath, _keyHolder);

    private string RawConfig() => File.ReadAllText(_configPath);

    private static McpServerConfig Server(string apiKey) => new()
    {
        Name = "YouTrack",
        Transport = McpTransport.Http,
        Url = "https://youtrack.invalid",
        Auth = McpServerAuth.ApiKey,
        ApiKey = apiKey,
    };

    [Fact]
    public async Task Enabling_LeavesNoReadableToken_RefusesTheWrongPassword_AndTheRightOneReadsItBack()
    {
        await Store().SaveAsync([Server(Token)]);
        Assert.Contains(Token, RawConfig());
        await Service().EnableAsync(Password);
        Assert.DoesNotContain(Token, RawConfig());
        Assert.Contains(SecretProtector.Prefix, RawConfig());

        // A fresh process: the key is gone, the file is ciphertext.
        var restarted = new SecretKeyHolder();
        var afterRestart = new SecretProtectionService(_configPath, restarted);
        Assert.Equal(new SecretProtectionStatus(Enabled: true, Unlocked: false), await afterRestart.GetStatusAsync());
        Assert.False(await afterRestart.UnlockAsync("not the password"));
        Assert.Null(restarted.Protector);

        Assert.True(await afterRestart.UnlockAsync(Password));

        var servers = await new McpServerStore(_configPath, restarted).LoadAsync();
        Assert.Equal(Token, servers.Single().ApiKey);
    }

    [Fact]
    public async Task TurningItOffAgain_PutsEveryCredentialBackInTheClear()
    {
        await Store().SaveAsync([Server(Token)]);

        var service = Service();
        await service.EnableAsync(Password);
        await service.DisableAsync();

        Assert.Contains(Token, RawConfig());
        Assert.DoesNotContain(SecretProtector.Prefix, RawConfig());
        Assert.Equal(Token, (await Store().LoadAsync()).Single().ApiKey);
    }

    [Fact]
    public async Task APluginsToken_NestedInsideItsOwnJson_IsEncryptedToo()
    {
        // How a plugin actually stores its settings: JSON inside a string, inside the cockpit's JSON. This is
        // where the YouTrack token lives, and a walker that only visited the outer document would leave it.
        var document = new JsonObject
        {
            ["Plugins"] = new JsonObject
            {
                ["youtrack"] = new JsonObject
                {
                    ["Data"] = new JsonObject
                    {
                        ["instances"] = $$"""[{"Url":"https://youtrack.invalid","Token":"{{Token}}"}]""",
                    },
                },
            },
        };
        await File.WriteAllTextAsync(_configPath, document.ToJsonString());

        await Service().EnableAsync(Password);

        Assert.DoesNotContain(Token, RawConfig());
    }

    [Fact]
    public async Task AnAlteredValue_FailsLoudly_RatherThanDecryptingToNonsense()
    {
        var protector = new SecretProtector(SecretKey.Derive(Password, SecretKey.NewSalt(), iterations: 1000));
        var encrypted = protector.Protect("McpServers[0].ApiKey", Token);

        // Same key, same ciphertext, different field: the path is the associated data, so a value cannot be
        // lifted out of one field and decrypted in another.
        var act = () => protector.Unprotect("McpServers[1].ApiKey", encrypted);

        Assert.Throws<SecretProtectionException>(act);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AForgottenPassword_HasAWayBackIn_WithoutLosingTheRestOfTheSettings()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        var restarted = new SecretKeyHolder();
        await new SecretProtectionService(_configPath, restarted).ResetForgottenPasswordAsync();

        var servers = await new McpServerStore(_configPath, restarted).LoadAsync();
        Assert.Equal("YouTrack", servers.Single().Name);
        Assert.Empty(servers.Single().ApiKey!);
        Assert.DoesNotContain("Security", RawConfig());
    }

    [Fact]
    public async Task AHalfEncryptedConfig_IsReadableAndHealsItself()
    {
        // How this file could exist: a restored backup, a hand edit, a migration from a version that wrote one.
        // Our own writes cannot produce it — the migration renames a finished file over the old one, and a rename
        // is atomic — but a file that ended up mixed anyway must not be a dead end.
        await Store().SaveAsync([Server(Token), Server("a-second-key")]);
        await Service().EnableAsync(Password);

        // Put the first server's key back in the clear, leaving the second encrypted: the half-and-half case.
        var document = JsonNode.Parse(RawConfig())!;
        document["McpServers"]![0]!["ApiKey"] = Token;
        await File.WriteAllTextAsync(_configPath, document.ToJsonString());

        var restarted = new SecretKeyHolder();
        Assert.True(await new SecretProtectionService(_configPath, restarted).UnlockAsync(Password));

        // Reading tolerates the mix: the encrypted one is decrypted, the plain one is passed through as it stands.
        var store = new McpServerStore(_configPath, restarted);
        var servers = await store.LoadAsync();
        Assert.Equal(Token, servers[0].ApiKey);
        Assert.Equal("a-second-key", servers[1].ApiKey);

        // And the next write closes the gap: everything that is not yet ciphertext becomes ciphertext.
        await store.SaveAsync(servers);

        Assert.DoesNotContain(Token, RawConfig());
        Assert.DoesNotContain("a-second-key", RawConfig());
    }

    [Fact]
    public async Task TheRewrite_KeepsABackup_ButScrubsThePlaintextOutOfIt()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        var backup = _configPath + ".bak";
        Assert.True(File.Exists(backup), "a migration that rewrites every credential leaves a way back");

        // The atomic swap keeps the pre-migration file as .bak — which is the operator's credentials still in the
        // clear, next door to the one that was just encrypted. Scrubbing it to ciphertext is the whole point:
        // otherwise the at-rest plaintext this feature removes would simply move one filename over.
        var backupText = File.ReadAllText(backup);
        Assert.DoesNotContain(Token, backupText);
        Assert.Contains(SecretProtector.Prefix, backupText);
    }

    [Fact]
    public async Task ChangingThePassword_DoesNotDeadlockOnTheSharedWriteGate()
    {
        await Store().SaveAsync([Server(Token)]);
        var service = Service();
        await service.EnableAsync(Password);

        const string newPassword = "a-different-good-passphrase";

        // The gate is non-reentrant, and ChangePassword re-enters through Disable then Enable: if it took the gate
        // itself it would deadlock until the 10s timeout. The WaitAsync turns that hang into a failing test.
        await service.ChangePasswordAsync(Password, newPassword).WaitAsync(TimeSpan.FromSeconds(20));

        var reopened = new SecretKeyHolder();
        Assert.True(await new SecretProtectionService(_configPath, reopened).UnlockAsync(newPassword));
        Assert.Equal(Token, (await new McpServerStore(_configPath, reopened).LoadAsync()).Single().ApiKey);
    }

    [Fact]
    public async Task ChangingThePassword_NeverWritesPlaintextToThePrimaryFile()
    {
        await Store().SaveAsync([Server(Token)]);

        // Every protect/unprotect during the rotation peeks at the live file: if the rotation ever wrote the
        // decrypted document to cockpit.json — the old Disable-then-Enable window (review #1) — this catches the
        // token sitting there mid-flight.
        var observing = false;
        var service = new SecretProtectionService(_configPath, _keyHolder, key => new ObservingProtector(
            new SecretProtector(key),
            () =>
            {
                if (observing)
                {
                    Assert.DoesNotContain(Token, File.ReadAllText(_configPath));
                }
            }));

        await service.EnableAsync(Password);

        observing = true;
        await service.ChangePasswordAsync(Password, "a-new-good-passphrase").WaitAsync(TimeSpan.FromSeconds(20));
        observing = false;

        var reopened = new SecretKeyHolder();
        Assert.True(await new SecretProtectionService(_configPath, reopened).UnlockAsync("a-new-good-passphrase"));
        Assert.Equal(Token, (await new McpServerStore(_configPath, reopened).LoadAsync()).Single().ApiKey);
    }

    [Fact]
    public async Task Enabling_AbortsAndLeavesTheFileByteIdentical_WhenAValueWillNotRoundTrip_AndTheBannerReturns()
    {
        await Store().SaveAsync([Server(Token)]);
        var before = RawConfig();

        // A protector whose ciphertext does not decrypt back: verify-before-publish must catch it and abort before
        // the atomic swap, leaving the plaintext config exactly as it was (review #2).
        var service = new SecretProtectionService(_configPath, _keyHolder, _ => new BrokenRoundTripProtector());

        await Assert.ThrowsAsync<SecretProtectionException>(() => service.EnableAsync(Password));

        Assert.Equal(before, RawConfig());
        Assert.DoesNotContain("Security", RawConfig());
        Assert.Null(_keyHolder.Protector);
        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "nothing was encrypted, so the banner is due again");
    }

    // AC-1879: Windows Hello stands in for the password through a wrap of the key; the fake signs like a TPM key would.
    [Fact]
    public async Task Hello_UnlocksWithItsOwnKey_ButNotWithAnotherOne_AndNeverWritesTheKeyInTheClear()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);
        var hello = new FakeSigner();

        Assert.True(await Service().EnableHelloAsync(Password, hello));

        var security = JsonNode.Parse(RawConfig())?["Security"] ?? throw new InvalidOperationException("No Security section.");
        var key = SecretKey.Derive(Password, Convert.FromBase64String((string?)security["Salt"] ?? ""), (int?)security["Iterations"] ?? 0);
        Assert.DoesNotContain(Convert.ToBase64String(key), RawConfig());
        var restarted = new SecretKeyHolder();
        Assert.Equal(HelloUnlockResult.Unlocked, await new SecretProtectionService(_configPath, restarted).UnlockWithHelloAsync(hello));
        Assert.Equal(Token, (await new McpServerStore(_configPath, restarted).LoadAsync()).Single().ApiKey);
        var otherMachine = new SecretKeyHolder();
        Assert.Equal(HelloUnlockResult.KeyGone, await new SecretProtectionService(_configPath, otherMachine).UnlockWithHelloAsync(new FakeSigner(presentElsewhere: true)));
        Assert.Null(otherMachine.Protector);
    }

    [Fact]
    public async Task ChangingThePassword_TurnsHelloOff_AndAnOldWrapPutBackDoesNotUnlock()
    {
        await Service().EnableAsync(Password);
        var hello = new FakeSigner();
        await Service().EnableHelloAsync(Password, hello);
        var oldHello = JsonNode.Parse(RawConfig())?["Security"]?["Hello"]?.DeepClone();

        await Service().ChangePasswordAsync(Password, "a-different-good-passphrase");
        Assert.False((await Service().GetStatusAsync()).HelloEnabled);
        var document = JsonNode.Parse(RawConfig()) ?? throw new InvalidOperationException("No config.");
        var security = document["Security"] as JsonObject ?? throw new InvalidOperationException("No Security section.");
        security["Hello"] = oldHello;
        File.WriteAllText(_configPath, document.ToJsonString());

        var restarted = new SecretKeyHolder();
        Assert.Equal(HelloUnlockResult.KeyGone, await new SecretProtectionService(_configPath, restarted).UnlockWithHelloAsync(hello));
        Assert.Null(restarted.Protector);
    }

    // Signs with an HMAC over a per-instance secret, like an OS key held in a TPM; `randomised` mimics RSA-PSS, and
    // `presentElsewhere` a key of the same name on another computer, which signs but with a different secret.
    private sealed class FakeSigner(bool randomised = false, bool presentElsewhere = false) : ISecretKeySigner
    {
        private readonly Dictionary<string, byte[]> _keys = [];

        public string? LastFailure => null;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async Task<SecretKeyEnrollment?> EnrollAsync(string name, byte[] challenge, CancellationToken cancellationToken = default)
        {
            _keys[name] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            return await SignAsync(name, challenge, cancellationToken) is { } signature
                ? new SecretKeyEnrollment(SecretKeyStorage.Hardware, signature)
                : null;
        }

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(presentElsewhere || _keys.ContainsKey(name));

        public Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default)
        {
            if (!_keys.TryGetValue(name, out var secret))
            {
                _keys[name] = secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            }

            var signature = randomised
                ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(256)
                : System.Security.Cryptography.HMACSHA256.HashData(secret, challenge);
            return Task.FromResult<byte[]?>(signature);
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            _keys.Remove(name);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AMigration_RacingAConcurrentStoreSave_LeavesEverythingEncrypted_WithNoSectionLost()
    {
        await Store().SaveAsync([Server(Token)]);

        // The migration and an ordinary store save hit the same file at once. They take the same write gate, so one
        // fully completes before the other starts — the result is never half-plaintext/half-ciphertext, and no
        // section is lost (review #3).
        var enable = Service().EnableAsync(Password);
        var save = Store().SaveAsync([Server(Token), Server("a-second-token")]);
        await Task.WhenAll(enable, save);

        Assert.DoesNotContain(Token, RawConfig());
        Assert.DoesNotContain("a-second-token", RawConfig());
        Assert.Contains("Security", RawConfig());
        Assert.Contains(SecretProtector.Prefix, RawConfig());

        var reopened = new SecretKeyHolder();
        Assert.True(await new SecretProtectionService(_configPath, reopened).UnlockAsync(Password));
        Assert.Equal(2, System.Linq.Enumerable.Count(await new McpServerStore(_configPath, reopened).LoadAsync()));
    }

    [Fact]
    public async Task StartupHousekeeping_RemovesAPlaintextBackup_WhenTheConfigIsEncrypted()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        // A plaintext .bak reappears (a crash, or an abandoned unlock that never re-encrypted). Startup runs before
        // any unlock — no key — but must still get the plaintext copy off disk (review #8).
        var backup = _configPath + ".bak";
        await File.WriteAllTextAsync(
            backup,
            new JsonObject { ["McpServers"] = new JsonArray(new JsonObject { ["ApiKey"] = Token }) }.ToJsonString());

        CredentialFileHousekeeping.RemoveEncryptedConfigPlaintextSidecars(_configPath);

        Assert.False(File.Exists(backup), "an encrypted config makes a plaintext backup pure exposure, so it is removed");
    }

    /// <summary>Wraps a real protector and fires a callback on every operation — the test's window onto what the
    /// primary file looks like mid-migration.</summary>
    private sealed class ObservingProtector(ISecretProtector inner, Action onOperate) : ISecretProtector
    {
        public string Protect(string path, string value)
        {
            onOperate();

            return inner.Protect(path, value);
        }

        public string Unprotect(string path, string value)
        {
            onOperate();

            return inner.Unprotect(path, value);
        }
    }

    /// <summary>Produces something that looks encrypted but does not decrypt back — the only way to exercise the
    /// verify-before-publish abort, since the real AES-GCM protector always round-trips.</summary>
    private sealed class BrokenRoundTripProtector : ISecretProtector
    {
        public string Protect(string path, string value) => SecretProtector.Prefix + value;

        public string Unprotect(string path, string value) => "not-what-went-in";
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
