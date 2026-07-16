using FluentAssertions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// The files the cockpit writes hold credentials — provider API keys, MCP bearer headers, the plugins' tokens —
/// so they are readable by their owner and nobody else. They were not: a plain File.Create leaves a file at the
/// umask, which on a stock Fedora means every account on the machine can read it, and the TTY session's
/// --mcp-config went to the world-writable temp directory and was never deleted at all.
/// <para>
/// Unix-only: Windows has no mode bits, and there the per-user profile directory is the equivalent boundary.
/// </para>
/// </summary>
public class CredentialFilePermissionTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-perm-{Guid.NewGuid():N}");

    public CredentialFilePermissionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ConfigFile_IsWrittenOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_directory, "cockpit.json");
        var store = new McpServerStore(path);

        await store.SaveAsync([new McpServerConfig { Name = "YouTrack", Transport = McpTransport.Http, Url = "https://example.invalid" }]);

        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
    }

    [Fact]
    public async Task ConfigFile_ThatIsAlreadyWorldReadable_IsRestrictedOnTheNextWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // What every existing installation looks like today: a config written by a version that let the umask decide.
        var path = Path.Combine(_directory, "cockpit.json");
        await File.WriteAllTextAsync(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await new McpServerStore(path).SaveAsync([]);

        File.GetUnixFileMode(path).Should().Be(OwnerOnly,
            "an operator should not have to hand-fix the permissions of a file we wrote wrong");
    }

    [Fact]
    public void TtyMcpConfig_IsWrittenOwnerOnly()
    {
        var path = TtyMcpConfigFile.Write("""{"mcpServers":{}}""", _directory);

        Path.GetDirectoryName(path).Should().Be(_directory);

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(OwnerOnly);
        }
    }

    [Fact]
    public void TtyMcpConfig_LivesBesideTheOtherState_NotInTheSharedTempDirectory()
    {
        // The file carries the registry's bearer headers, and the temp directory is world-readable (1777).
        var temporaryDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

        Path.GetFullPath(TtyMcpConfigFile.DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Should().NotBe(temporaryDirectory);
    }

    [Fact]
    public void TtyMcpConfig_IsDeletedWhenTheSessionIsDisposed()
    {
        var path = TtyMcpConfigFile.Write("""{"mcpServers":{}}""", _directory);

        using (var session = new TtyProcessOwningSessionFiles(new FakeConPtyProcess(), [path]))
        {
            File.Exists(path).Should().BeTrue("the CLI reads it while the session is alive");
        }

        File.Exists(path).Should().BeFalse("a credential must not outlive the session that needed it");
    }

    [Fact]
    public void SweepStale_RemovesWhatACrashOrAnOlderVersionLeftBehind()
    {
        var temporaryDirectory = Path.Combine(_directory, "tmp");
        Directory.CreateDirectory(temporaryDirectory);

        var ours = TtyMcpConfigFile.Write("""{"mcpServers":{}}""", _directory);
        var legacy = Path.Combine(temporaryDirectory, $"cockpit-tty-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, """{"mcpServers":{}}""");
        var unrelated = Path.Combine(temporaryDirectory, "something-else.json");
        File.WriteAllText(unrelated, "{}");

        TtyMcpConfigFile.SweepStale(_directory, temporaryDirectory);

        File.Exists(ours).Should().BeFalse("a killed session leaves its config behind");
        File.Exists(legacy).Should().BeFalse("the previous implementation's files are the ones holding a live token today");
        File.Exists(unrelated).Should().BeTrue("the sweep only claims its own files");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeConPtyProcess : IConPtyProcess
    {
        public Stream InputStream { get; } = Stream.Null;

        public Stream OutputStream { get; } = Stream.Null;

        public int ProcessId => 0;

        public void Resize(short columns, short rows)
        {
        }

        public void Dispose()
        {
        }
    }
}
