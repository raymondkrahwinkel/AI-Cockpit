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

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
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

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void TtyMcpConfig_LivesBesideTheOtherState_NotInTheSharedTempDirectory()
    {
        // The file carries the registry's bearer headers, and the temp directory is world-readable (1777).
        var temporaryDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

        Assert.NotEqual(temporaryDirectory,
            Path.GetFullPath(TtyMcpConfigFile.DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar));
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
