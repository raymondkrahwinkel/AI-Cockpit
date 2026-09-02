namespace Cockpit.Core.Tests;

/// <summary>
/// The host-side PATH probe (AC-510[b]): a bare command name resolves against a real file on PATH, with Windows
/// extension probing for an npm-style <c>.cmd</c> shim, and it says only "found" — an empty, non-executable file
/// still resolves, since this probe never spawns or validates anything.
/// </summary>
public sealed class HostExecutableProbeTests : IDisposable
{
    private readonly string _dir;

    public HostExecutableProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cockpit-host-executable-probe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Resolve_BareNameOnPath_ReturnsItsAbsolutePath()
    {
        // An empty file with no PE header still resolves — the doc-comment's "found, never works" guarantee:
        // nothing here spawns or validates the file, it only checks that one exists by that name.
        var claude = Touch(OperatingSystem.IsWindows() ? "claude.exe" : "claude");

        Assert.Equal(claude, HostExecutableProbe.Resolve("claude", _dir));
    }

    [Fact]
    public void Resolve_NotOnPath_ReturnsNull()
    {
        Assert.Null(HostExecutableProbe.Resolve("codex", _dir));
    }

    [Fact]
    public void Resolve_RootedExistingPath_ReturnsItUnchanged()
    {
        var gemini = Touch("gemini.exe");

        Assert.Equal(gemini, HostExecutableProbe.Resolve(gemini, pathVariable: string.Empty));
    }

    [Fact]
    public void Resolve_RootedMissingPath_ReturnsNull()
    {
        var missing = Path.Combine(_dir, "not-here.exe");

        Assert.Null(HostExecutableProbe.Resolve(missing, pathVariable: string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankCommand_ReturnsNull(string command)
    {
        Assert.Null(HostExecutableProbe.Resolve(command, _dir));
    }

    [WindowsFact("Only Windows resolves a bare name through a .cmd shim on PATHEXT.")]
    public void Resolve_Windows_FindsACmdShimForABareName()
    {
        // npm-style installs (codex, claude) commonly land as a .cmd shim; Process does no PATHEXT lookup for a
        // bare name, so the probe must try the extension itself.
        var codex = Touch("codex.cmd");

        Assert.Equal(codex, HostExecutableProbe.Resolve("codex", _dir));
    }

    [Fact]
    public void Resolve_PublicOverload_OnlyEverReturnsAPathThatReallyExists()
    {
        // Against this process's real PATH: a machine with none of these installed returning null for all of
        // them is also a pass — the only contract asserted is that a non-null answer is never a lie.
        foreach (var command in new[] { "claude", "codex", "gemini" })
        {
            if (HostExecutableProbe.Resolve(command) is { } resolved)
            {
                Assert.True(File.Exists(resolved));
            }
        }
    }
}
