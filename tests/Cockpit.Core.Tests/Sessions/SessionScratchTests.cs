using System.Diagnostics;
using System.Runtime.Versioning;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

[UnsupportedOSPlatform("windows")]
public sealed class SessionScratchTests : IDisposable
{
    private readonly string? _previousBase = Environment.GetEnvironmentVariable(SessionScratch.BaseVariable);
    private readonly string? _previousGroup = Environment.GetEnvironmentVariable("COCKPIT_AGENT_GROUP");
    private readonly string _basis = Path.Combine(Path.GetTempPath(), $"ac-1989-{Guid.NewGuid():N}");
    private const UnixFileMode OwnerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public SessionScratchTests()
    {
        Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, _basis);
        Environment.SetEnvironmentVariable("COCKPIT_AGENT_GROUP", null);
    }

    // AC-1989, layer 2: private desktop scratch and agent-group sharing on the base, new pane and reused pane.
    [UnixFact("Unix file permissions")]
    public void Create_SharesOnlyWithConfiguredAgentGroup_AndKeepsDesktopScratchPrivate()
    {
        var privatePath = SessionScratch.Create("private");
        Assert.Equal(OwnerMode, File.GetUnixFileMode(privatePath));
        var gid = _ReadCommand("id", "-g");

        Environment.SetEnvironmentVariable("COCKPIT_AGENT_GROUP", gid);
        var sharedPath = SessionScratch.Create("shared");
        var reusedPath = SessionScratch.Create("private");

        Assert.Equal(privatePath, reusedPath);
        _AssertShared(_basis, gid);
        _AssertShared(sharedPath, gid);
        _AssertShared(reusedPath, gid);
    }

    private static void _AssertShared(string path, string gid)
    {
        Assert.Equal(OwnerMode | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(path));
        Assert.Equal(gid, _ReadCommand("stat", OperatingSystem.IsMacOS() ? "-f" : "-c", "%g", path));
    }

    private static string _ReadCommand(string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, _previousBase);
        Environment.SetEnvironmentVariable("COCKPIT_AGENT_GROUP", _previousGroup);
        Directory.Delete(_basis, recursive: true);
    }
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string unixOnly) => Skip = OperatingSystem.IsWindows() ? unixOnly : null;
}
