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
    public async Task Enabling_LeavesNoReadableTokenInTheFile()
    {
        await Store().SaveAsync([Server(Token)]);
        Assert.Contains(Token, RawConfig());

        await Service().EnableAsync(Password);

        Assert.DoesNotContain(Token, RawConfig());
        Assert.Contains(SecretProtector.Prefix, RawConfig());
    }

    [Fact]
    public async Task AnUnlockedApp_ReadsTheCredentialBack()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        // A fresh process: the key is gone, the file is ciphertext.
        var restarted = new SecretKeyHolder();
        var afterRestart = new SecretProtectionService(_configPath, restarted);
        Assert.Equal(new SecretProtectionStatus(Enabled: true, Unlocked: false), await afterRestart.GetStatusAsync());

        Assert.True(await afterRestart.UnlockAsync(Password));

        var servers = await new McpServerStore(_configPath, restarted).LoadAsync();
        Assert.Equal(Token, servers.Single().ApiKey);
    }

    [Fact]
    public async Task TheWrongPassword_IsRefusedRatherThanReturningNonsense()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        var restarted = new SecretKeyHolder();

        Assert.False(await new SecretProtectionService(_configPath, restarted).UnlockAsync("not the password"));
        Assert.Null(restarted.Protector);
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
    public async Task TheMigration_ReportsProgressWithATotalItActuallyKnows()
    {
        await Store().SaveAsync([Server(Token), Server("a-second-key")]);

        // A synchronous observer, not Progress<T>: that one hands its callbacks to the thread pool, so a test
        // built on it would be asserting the scheduler's ordering rather than the migration's.
        var reports = new List<SecretMigrationProgress>();
        await Service().EnableAsync(Password, new SynchronousProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.True(report.Total == 2));
        Assert.Equal(new[] { 0, 1, 2 }, reports.Select(report => report.Completed));
    }

    private sealed class SynchronousProgress(Action<SecretMigrationProgress> report) : IProgress<SecretMigrationProgress>
    {
        public void Report(SecretMigrationProgress value) => report(value);
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
    public async Task APluginsOwnFieldName_IsProtectedOnceThePluginSaysSo()
    {
        // "pat" is not a name the host recognises — that is the whole reason a plugin can declare it (plugin.json
        // "secretKeys", or IPluginStorage.SetSecret). Without the declaration it would be stored in the clear
        // while the operator believes their credentials are encrypted, which is worse than not offering the
        // feature at all.
        var declared = new SecretKeyHolder();
        declared.Declare(["pat"]);

        var document = new JsonObject
        {
            ["Plugins"] = new JsonObject
            {
                ["some-plugin"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["pat"] = Token },
                },
            },
        };
        await File.WriteAllTextAsync(_configPath, document.ToJsonString());

        await new SecretProtectionService(_configPath, declared).EnableAsync(Password);

        Assert.DoesNotContain(Token, RawConfig());

        // And a fresh start, which knows the declared name from the config, reads it back rather than handing the
        // plugin ciphertext.
        var restarted = new SecretKeyHolder();
        restarted.Declare(["pat"]);
        Assert.True(await new SecretProtectionService(_configPath, restarted).UnlockAsync(Password));

        var protector = restarted.Protector!;
        var stored = JsonNode.Parse(RawConfig())!["Plugins"]!["some-plugin"]!["Data"]!["pat"]!.GetValue<string>();
        Assert.Equal(Token, protector.Unprotect("Plugins.some-plugin.Data.pat", stored));
    }

    [Fact]
    public void AnEncryptedValue_IsNeverEncryptedTwice()
    {
        var protector = new SecretProtector(SecretKey.Derive(Password, SecretKey.NewSalt(), iterations: 1000));
        var once = protector.Protect("McpServers[0].ApiKey", Token);

        var twice = protector.Protect("McpServers[0].ApiKey", once);

        Assert.Equal(once, twice);
        Assert.Equal(Token, protector.Unprotect("McpServers[0].ApiKey", twice));
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
    public void TheFormat_IsAFormat_NotSomethingOnlyItsOwnWriterCanRead()
    {
        // Produced by an independent implementation (Python: hashlib.pbkdf2_hmac + cryptography's AESGCM) from the
        // password, salt and iteration count below. It pins the on-disk format — the KDF, the key length, the
        // nonce|ciphertext|tag layout, and the field path as associated data. Change any of those and this fails,
        // which is the point: an operator's config must still open after we touch the crypto.
        const string encrypted = "enc:v1:AAECAwQFBgcICQoLe8UEzFFflAnzBRRyc23dvImugNtoqJxQAip0oQYBFV+1s14upA==";
        var salt = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODw==");

        var protector = new SecretProtector(SecretKey.Derive(Password, salt, iterations: 1000));

        Assert.Equal(Token, protector.Unprotect("McpServers[0].ApiKey", encrypted));
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
    public async Task TheBanner_Warns_WhenCredentialsSitInTheClear()
    {
        await Store().SaveAsync([Server(Token)]);

        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "encryption is off and there is a token in the file to protect");
    }

    [Fact]
    public async Task TheBanner_StaysQuiet_WhenThereIsNoCredentialToProtect()
    {
        await File.WriteAllTextAsync(_configPath, new JsonObject { ["Profiles"] = new JsonArray() }.ToJsonString());

        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a config with no credential in it has nothing to warn about");
    }

    [Fact]
    public async Task TheBanner_StaysQuiet_OnceEncryptionIsOn()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "the credentials are encrypted, so there is nothing left to warn about");
    }

    [Fact]
    public async Task DismissingTheWarning_Silences_ItAndSurvivesARestart()
    {
        await Store().SaveAsync([Server(Token)]);

        await Service().DismissUnprotectedWarningAsync();

        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "the operator said not this set");

        // A fresh process reads the dismissal back rather than nagging again — it is persisted, not per-session.
        var restarted = new SecretProtectionService(_configPath, new SecretKeyHolder());
        Assert.False((await restarted.GetStatusAsync()).ShouldWarnUnprotected, "the dismissal outlives the run that made it");
    }

    [Fact]
    public async Task RotatingACredential_OnAFieldThatWasAlreadyThere_DoesNotBringTheWarningBack()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().DismissUnprotectedWarningAsync();

        // Same field, new value: a key rotation. The dismissal is bound to which fields hold a credential, not to
        // what is in them, so this must not un-dismiss.
        await Store().SaveAsync([Server("a-rotated-token")]);

        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a rotated value on an existing field is not a new credential");
    }

    [Fact]
    public async Task AddingANewCredential_AtANewPath_BringsTheWarningBack()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().DismissUnprotectedWarningAsync();

        // A second server is a new credential field — a new path — so the set changes and the banner returns.
        await Store().SaveAsync([Server(Token), Server("a-second-token")]);

        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a brand-new credential is exactly when the operator should be reminded again");
    }

    [Fact]
    public async Task TurningEncryptionOff_BringsTheWarningBackAtOnce()
    {
        await Store().SaveAsync([Server(Token)]);
        var service = Service();
        await service.EnableAsync(Password);

        // Dismissal from before encryption must not carry over: Disable puts every credential back in the clear, so
        // the banner should return immediately (Raymond, 2026-07-19).
        await service.DisableAsync();

        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a deliberate Disable exposes the credentials again, so the warning returns");
    }

    [Fact]
    public async Task TheDismissalRecord_CarriesFieldPaths_NotCredentialValues()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().DismissUnprotectedWarningAsync();

        // The token itself is still in the file — encryption is off, that is what the banner was about — but the
        // dismissal we just wrote records only the field paths, so it must not carry the credential's value.
        var dismissedPaths = JsonNode.Parse(RawConfig())!["SecurityNotice"]!["DismissedPaths"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToList();
        Assert.Equal("McpServers[0].ApiKey", Assert.Single(dismissedPaths));
        Assert.DoesNotContain(dismissedPaths, path => path.Contains(Token));
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
    public async Task Enabling_ScrubsThePlaintextOutOfADamagedSidecar()
    {
        await Store().SaveAsync([Server(Token)]);

        // A quarantined copy an earlier recovery left behind (CockpitConfigFileAccess writes these) — plaintext,
        // and holding the same token as the live config. Decision #4: Enable has to close it too.
        var damaged = $"{_configPath}.damaged-20260719-101500";
        await File.WriteAllTextAsync(damaged, new JsonObject { ["token"] = Token }.ToJsonString());

        await Service().EnableAsync(Password);

        Assert.True(!File.Exists(damaged) || !File.ReadAllText(damaged).Contains(Token),
            "a plaintext .damaged-* copy is re-encrypted or removed, never left with a readable token");
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
    public async Task Enabling_AbortsAndLeavesTheFileByteIdentical_WhenAValueWillNotRoundTrip()
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
    }

    [Fact]
    public async Task AnAbortedMigration_LeavesThePlaintextIntact_AndTheBannerReturns()
    {
        await Store().SaveAsync([Server(Token)]);

        var service = new SecretProtectionService(_configPath, _keyHolder, _ => new BrokenRoundTripProtector());
        await Assert.ThrowsAnyAsync<SecretProtectionException>(() => service.EnableAsync(Password));

        Assert.Contains(Token, RawConfig());
        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "nothing was encrypted, so the banner is due again");
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
    public async Task Unlocking_ReEncryptsAPlaintextBackup_LeftBesideAnEncryptedConfig()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        // A plaintext .bak reappears (a crash between Save and scrub). A fresh start unlocks, and the sweep closes
        // it (review #4).
        await File.WriteAllTextAsync(
            _configPath + ".bak",
            new JsonObject { ["McpServers"] = new JsonArray(new JsonObject { ["ApiKey"] = Token }) }.ToJsonString());

        var restarted = new SecretKeyHolder();
        Assert.True(await new SecretProtectionService(_configPath, restarted).UnlockAsync(Password));

        var backup = File.ReadAllText(_configPath + ".bak");
        Assert.DoesNotContain(Token, backup);
        Assert.Contains(SecretProtector.Prefix, backup);
    }

    [Fact]
    public async Task SavingACredential_RaisesTheAwarenessSignalOnlyWhenItIsAnAtRestExposure()
    {
        var holder = new SecretKeyHolder();
        var fires = 0;
        holder.UnprotectedSecretsWritten += (_, _) => fires++;
        var store = new McpServerStore(_configPath, holder);

        // (a) a credential written in the clear → the banner is nudged (review #5).
        await store.SaveAsync([Server(Token)]);
        Assert.Equal(1, fires);

        // (b) a save with no credential in it → no nudge.
        await store.SaveAsync([ServerWithoutKey()]);
        Assert.Equal(1, fires);

        // (c) a save while unlocked (protector present, ciphertext on disk) → no nudge.
        holder.Unlock(new SecretProtector(SecretKey.Derive(Password, SecretKey.NewSalt(), iterations: 1000)));
        await store.SaveAsync([Server("another-token")]);
        Assert.Equal(1, fires);
    }

    [Fact]
    public async Task RemovingACredential_DoesNotReNag_ButAddingANewPathDoes()
    {
        // Two named credential fields, so a removal and an addition are each an unambiguous change to the path set.
        await WriteRawSecretsAsync(("token", Token), ("secret", "a-second-credential"));
        await Service().DismissUnprotectedWarningAsync();
        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "just dismissed");

        // Remove one of the two dismissed fields: the remaining set is still a subset of what was dismissed.
        await WriteRawSecretsAsync(("token", Token));
        Assert.False((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a removal is not a new credential (review #7)");

        // Add a genuinely new field path → the banner returns.
        await WriteRawSecretsAsync(("token", Token), ("password", "a-third-credential"));
        Assert.True((await Service().GetStatusAsync()).ShouldWarnUnprotected, "a new credential path re-nags");
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

    private async Task WriteRawSecretsAsync(params (string Key, string Value)[] fields)
    {
        var document = new JsonObject();

        // Carry the dismissal across, the way a real typed store save round-trips the SecurityNotice section rather
        // than clobbering it — otherwise "remove a field" would also wipe the dismissal and re-nag for that reason.
        if ((File.Exists(_configPath) ? JsonNode.Parse(File.ReadAllText(_configPath)) : null) is JsonObject existing
            && existing["SecurityNotice"] is { } notice)
        {
            document["SecurityNotice"] = notice.DeepClone();
        }

        foreach (var (key, value) in fields)
        {
            document[key] = value;
        }

        await File.WriteAllTextAsync(_configPath, document.ToJsonString());
    }

    private static McpServerConfig ServerWithoutKey() => new()
    {
        Name = "OpenServer",
        Transport = McpTransport.Http,
        Url = "https://open.invalid",
        Auth = McpServerAuth.None,
    };

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
