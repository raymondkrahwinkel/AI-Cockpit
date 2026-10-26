using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1711. Criterion 2 by hand as well: after a real start no port of the process tree listens (netstat, see the PR).
public class BrowserEngineTests
{
    // L2b (boundary): a debugging port would open every tab of every profile in the process to the whole machine.
    [Fact]
    public void TheEngineSettings_OpenNoDebuggingPort_AndIgnoreTheProcessCommandLine()
    {
        var settings = BrowserEngine.CreateSettings("runtime", "subprocess", "cache");

        Assert.Equal(0, settings.RemoteDebuggingPort);
        Assert.True(settings.CommandLineArgsDisabled);
        Assert.DoesNotContain(BrowserEngine.Switches, entry => entry.Key.StartsWith("remote-debugging", StringComparison.Ordinal));
    }

    // L2d (platform/fallback): an engine that cannot load is a reason on the panel, never an exception in Cockpit.
    [Fact]
    public async Task AnEngineThatCannotLoad_IsUnavailableWithTheReason_WithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-engine-test-" + Guid.NewGuid().ToString("N"));
        var runtime = Directory.CreateDirectory(Path.Combine(root, "runtime")).FullName;
        var subprocess = Path.Combine(root, "subprocess.exe");
        try
        {
            File.WriteAllBytes(subprocess, []);
            var engine = new BrowserEngine((_, _) => Task.FromResult(new CefRuntimeResult(runtime, null)), subprocess, Path.Combine(root, "cache"));

            await engine.EnsureStartedAsync();

            Assert.Equal(BrowserEngineState.Unavailable, engine.State);
            Assert.False(string.IsNullOrWhiteSpace(engine.Reason));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
