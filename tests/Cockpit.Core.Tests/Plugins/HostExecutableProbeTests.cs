using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

public sealed class HostExecutableProbeTests : IDisposable
{
    private readonly string _userProfile = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_FindsClaudeInItsOffPathNativeInstallerDirectory()
    {
        var localBin = Path.Combine(_userProfile, ".local", "bin");
        Directory.CreateDirectory(localBin);
        var expected = Path.Combine(localBin, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(expected, string.Empty);

        var executable = Assert.IsType<ProviderHostExecutable>(ProviderHostExecutables.For("claude-provider"));

        Assert.Equal(expected, HostExecutableProbe.Resolve(executable, pathVariable: string.Empty, userProfile: _userProfile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_userProfile))
        {
            Directory.Delete(_userProfile, recursive: true);
        }
    }
}
