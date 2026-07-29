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

    [Fact]
    public void SessionScopedFile_IsDeletedWhenTheSessionIsDisposed()
    {
        // The host-side --mcp-config writer this used to exercise (TtyMcpConfigFile.Write) had no production
        // caller and was removed in AC-380 — each provider plugin now writes and owns its own session-scoped
        // file (e.g. ClaudeMcpConfig). What still matters, and is still live in production, is that
        // TtyProcessOwningSessionFiles deletes whatever session-scoped file it is handed once the session ends.
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"mcpServers":{}}""");

        using (var session = new TtyProcessOwningSessionFiles(new FakeConPtyProcess(), [path]))
        {
            Assert.True(File.Exists(path), "the CLI reads it while the session is alive");
        }

        Assert.False(File.Exists(path), "a credential must not outlive the session that needed it");
    }

    [Fact]
    public void SweepStale_RemovesWhatACrashOrAnOlderVersionLeftBehind()
    {
        var temporaryDirectory = Path.Combine(_directory, "tmp");
        Directory.CreateDirectory(temporaryDirectory);

        // TtyMcpConfigFile no longer writes these itself (AC-380) — simulating what an older cockpit version, or
        // a killed session, left behind is now the only way to put one here.
        var ours = Path.Combine(_directory, $"tty-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(ours, """{"mcpServers":{}}""");
        var legacy = Path.Combine(temporaryDirectory, $"cockpit-tty-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, """{"mcpServers":{}}""");
        var unrelated = Path.Combine(temporaryDirectory, "something-else.json");
        File.WriteAllText(unrelated, "{}");

        TtyMcpConfigFile.SweepStale(_directory, temporaryDirectory);

        Assert.False(File.Exists(ours), "a killed session leaves its config behind");
        Assert.False(File.Exists(legacy), "the previous implementation's files are the ones holding a live token today");
        Assert.True(File.Exists(unrelated), "the sweep only claims its own files");
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
