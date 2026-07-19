using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Security;

/// <summary>
/// AC-5 is a pure UI lock: when the OS screen locks the cockpit puts the unlock screen in front, but the encryption
/// key stays in memory. This pins Raymond's principle — an agent that is already running must keep working while the
/// screen is up, so a background config write goes through and stays encrypted rather than being blocked. It is the
/// exact opposite of the write-refused behaviour an earlier round built and this branch removed.
/// </summary>
public sealed class ScreenLockKeepsKeyTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Token = "perm:a-youtrack-token";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-uilock-{Guid.NewGuid():N}");
    private readonly SecretKeyHolder _holder = new();

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public ScreenLockKeepsKeyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static McpServerConfig Server(string apiKey) => new()
    {
        Name = "YouTrack",
        Transport = McpTransport.Http,
        Url = "https://youtrack.invalid",
        Auth = McpServerAuth.ApiKey,
        ApiKey = apiKey,
    };

    [Fact]
    public async Task AConfigWrite_SucceedsWhileTheUnlockScreenIsUp_AndStaysEncrypted()
    {
        var service = new SecretProtectionService(ConfigPath, _holder);
        await service.EnableAsync(Password);

        // The UI lock never touches the holder — this is what "the screen is up" looks like to the write seam: the
        // app is still unlocked, the key is still in memory. Assert that precondition so the test cannot silently pass
        // for the wrong reason (a key wipe sneaking back in).
        _holder.Protector.Should().NotBeNull("a UI lock leaves the encryption key in memory");

        // A background writer — a running agent adding an MCP credential — writes a whole section through the shared
        // seam while the operator stares at the unlock screen. Earlier this was refused; now it must go through.
        var store = new McpServerStore(ConfigPath, _holder);
        var write = async () => await store.SaveAsync([Server(Token)]);

        await write.Should().NotThrowAsync("a UI lock must not block a running agent's config write");

        // And because the key was present, the credential landed encrypted, not in the clear — nothing leaked.
        File.ReadAllText(ConfigPath).Should().NotContain(Token, "the key was in memory, so the write went out encrypted");
        (await store.LoadAsync()).Single().ApiKey.Should().Be(Token, "it reads back through the same key");
    }
}
