using Cockpit.Plugin.Kind.Cli;

namespace Cockpit.Plugin.Kind.Tests;

// Detection of the `kind` binary (AC-179 criterion 2) — a PATH-probe with a deadline, mirroring LocalCiRuntime's
// act-detection. Real process, no fake runner needed: "kind is not installed" is exactly the not-started case
// CliRunner already proves in CliRunnerTests.
public class KindRuntimeTests
{
    private sealed class PosixFactAttribute : FactAttribute
    {
        public PosixFactAttribute() => Skip = OperatingSystem.IsWindows()
            ? "This test emulates a POSIX kind probe; cmd /c is covered by CliRunnerTests."
            : null;
    }

    [Fact]
    public async Task NotOnPath_ComesBackAsNotInstalled()
    {
        var runtime = new KindRuntime(new CliRunner(), executableName: "kind-no-such-executable-anywhere");

        var status = await runtime.DetectAsync(CancellationToken.None);

        Assert.False(status.IsInstalled);
        Assert.Null(status.Version);
        Assert.Contains("was not found on PATH", status.Message);
    }

    [PosixFact]
    public async Task RepeatedDetection_OnlyProbesOnceAfterSuccess()
    {
        // Each invocation appends one line to a temp file — a call count that needs no CliRunner test seam.
        var counterFile = Path.GetTempFileName();
        try
        {
            var (fileName, arguments) = _ShellCommand($"echo call >> \"{counterFile}\" && echo kind v0.23.0 go1.22.1 linux/amd64");
            var runtime = new KindRuntime(new CliRunner(), fileName, arguments);

            var first = await runtime.DetectAsync(CancellationToken.None);
            var second = await runtime.DetectAsync(CancellationToken.None);

            Assert.True(first.IsInstalled);
            Assert.Equal("linux/amd64", first.Version);
            Assert.True(second.IsInstalled);
            Assert.Single(await File.ReadAllLinesAsync(counterFile));
        }
        finally
        {
            File.Delete(counterFile);
        }
    }

    [Fact]
    public async Task AFailedProbe_IsNeverCached()
    {
        var runtime = new KindRuntime(new CliRunner(), executableName: "kind-no-such-executable-anywhere");

        await runtime.DetectAsync(CancellationToken.None);
        var second = await runtime.DetectAsync(CancellationToken.None);

        Assert.False(second.IsInstalled);
    }

    private static (string FileName, string[] Arguments) _ShellCommand(string script) =>
        OperatingSystem.IsWindows() ? ("cmd", ["/c", script]) : ("sh", ["-c", script]);
}
