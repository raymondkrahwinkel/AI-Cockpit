using Cockpit.Plugin.Kind.Cli;

namespace Cockpit.Plugin.Kind.Tests;

// The process wrapper every kind invocation runs through (AC-179): argv, both pipes drained while the process
// runs, and the not-started case that is how "kind is not installed" is learned.
public class CliRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task LockedEnvironment_OverridesTheAmbientProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("CLIRUNNER_TEST_VAR", "attacker-context");
        try
        {
            var (fileName, arguments) = _ShellCommand(_PrintEnvVarCommand("CLIRUNNER_TEST_VAR"));
            var command = new CliCommand(fileName, arguments, new Dictionary<string, string> { ["CLIRUNNER_TEST_VAR"] = "locked-value" });

            var result = await new CliRunner().RunAsync(command, Generous);

            Assert.True(result.Succeeded);
            Assert.Contains("locked-value", result.Stdout);
            Assert.DoesNotContain("attacker-context", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIRUNNER_TEST_VAR", null);
        }
    }

    private static (string FileName, string[] Arguments) _ShellCommand(string script) =>
        OperatingSystem.IsWindows() ? ("cmd", ["/c", script]) : ("sh", ["-c", script]);

    private static string _PrintEnvVarCommand(string name) =>
        OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";
}
