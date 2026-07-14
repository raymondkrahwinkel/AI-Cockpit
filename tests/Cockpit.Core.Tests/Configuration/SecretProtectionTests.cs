using System.Text.Json.Nodes;
using FluentAssertions;
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
        RawConfig().Should().Contain(Token, "this is what the file looks like today");

        await Service().EnableAsync(Password);

        RawConfig().Should().NotContain(Token, "the whole point is that the token is not sitting there to be read");
        RawConfig().Should().Contain(SecretProtector.Prefix);
    }

    [Fact]
    public async Task AnUnlockedApp_ReadsTheCredentialBack()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        // A fresh process: the key is gone, the file is ciphertext.
        var restarted = new SecretKeyHolder();
        var afterRestart = new SecretProtectionService(_configPath, restarted);
        (await afterRestart.GetStatusAsync()).Should().Be(new SecretProtectionStatus(Enabled: true, Unlocked: false));

        (await afterRestart.UnlockAsync(Password)).Should().BeTrue();

        var servers = await new McpServerStore(_configPath, restarted).LoadAsync();
        servers.Single().ApiKey.Should().Be(Token);
    }

    [Fact]
    public async Task TheWrongPassword_IsRefusedRatherThanReturningNonsense()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        var restarted = new SecretKeyHolder();

        (await new SecretProtectionService(_configPath, restarted).UnlockAsync("not the password")).Should().BeFalse();
        restarted.Protector.Should().BeNull("a refused password must not leave the app half-unlocked");
    }

    [Fact]
    public async Task TurningItOffAgain_PutsEveryCredentialBackInTheClear()
    {
        await Store().SaveAsync([Server(Token)]);

        var service = Service();
        await service.EnableAsync(Password);
        await service.DisableAsync();

        RawConfig().Should().Contain(Token, "nothing may be lost by changing your mind");
        RawConfig().Should().NotContain(SecretProtector.Prefix);
        (await Store().LoadAsync()).Single().ApiKey.Should().Be(Token);
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

        RawConfig().Should().NotContain(Token);
    }

    [Fact]
    public async Task AnAlteredValue_FailsLoudly_RatherThanDecryptingToNonsense()
    {
        var protector = new SecretProtector(SecretKey.Derive(Password, SecretKey.NewSalt(), iterations: 1000));
        var encrypted = protector.Protect("McpServers[0].ApiKey", Token);

        // Same key, same ciphertext, different field: the path is the associated data, so a value cannot be
        // lifted out of one field and decrypted in another.
        var act = () => protector.Unprotect("McpServers[1].ApiKey", encrypted);

        act.Should().Throw<SecretProtectionException>();
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

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(report => report.Total == 2, "a bar that does not know its total cannot show progress");
        reports.Select(report => report.Completed).Should().Equal(0, 1, 2);
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
        servers.Single().Name.Should().Be("YouTrack", "the server itself survives — only its credential is gone");
        servers.Single().ApiKey.Should().BeEmpty();
        RawConfig().Should().NotContain("Security", "encryption is off again, so the app starts without asking for a password");
    }

    [Fact]
    public async Task TheRewrite_KeepsABackupOfWhatWasThere()
    {
        await Store().SaveAsync([Server(Token)]);
        await Service().EnableAsync(Password);

        File.Exists(_configPath + ".bak").Should().BeTrue("a migration that rewrites every credential leaves a way back");
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
