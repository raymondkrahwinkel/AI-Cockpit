using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Shell;

// The `cockpit-coding-shell` MCP tool (AC-1489): opencode's synchronous shell for a model on an HTTP provider, so it
// runs a build or a test and reads the outcome in one call instead of polling start_run. Named `shell`, not `bash`:
// on Windows it is PowerShell, and under the name bash a model writes bash syntax that fails there.
internal sealed class CodingShellTools(ISessionRegistry sessions, IShellCommandRunner runner)
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;

    [McpServerTool(Name = "shell", ReadOnly = false, Destructive = true)]
    [Description("Runs one command line and waits for it to finish. On Windows the shell is PowerShell (pwsh), elsewhere bash: write the command in that syntax. Runs in your working directory or `workdir` inside it. Returns the exit code and the end of the output (at most 50 KiB or 2000 lines). A run past `timeout` is stopped with everything it started.")]
    public async Task<string> Shell(
        [Description("The command line to run.")] string command,
        [Description("How long to wait, in milliseconds. Default 120000, at most 600000.")] int? timeout = null,
        [Description("The directory to run in, relative to your working directory. Default: the working directory itself.")] string? workdir = null)
    {
        if (!CodingTools.TryResolve(CodingTools.RootFor(sessions), workdir ?? ".", out var directory, out var error))
        {
            return error;
        }

        if (!Directory.Exists(directory))
        {
            return $"Error: no such directory: {workdir}";
        }

        var (shell, arguments) = OperatingSystem.IsWindows()
            ? ("pwsh", new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command })
            : ("/bin/bash", new[] { "-lc", command });
        ShellCommandResult result;
        try
        {
            result = await runner.RunAsync(directory, shell, arguments, TimeSpan.FromMilliseconds(Math.Clamp(timeout ?? DefaultTimeoutMs, 1, MaxTimeoutMs))).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return $"Error: could not start {shell}: {ex.Message}";
        }

        var output = string.IsNullOrEmpty(result.StandardError) ? result.StandardOutput : $"{result.StandardOutput}\n[stderr]\n{result.StandardError}";
        var status = result.TimedOut ? $"Timed out after {result.Duration.TotalSeconds:0.#}s and was stopped." : $"Exit code {result.ExitCode}.";
        return $"{status}\n{_Tail(output)}";
    }

    // The end of the output, where a build or test run puts its verdict, within opencode's 50 KiB / 2000-line cap.
    private static string _Tail(string text)
    {
        var start = Math.Max(0, text.Length - CodingTools.MaxChars);
        var lines = 0;
        for (var index = text.Length - 1; index > start; index--)
        {
            if (text[index] == '\n' && ++lines == CodingTools.MaxLines)
            {
                start = index + 1;
                break;
            }
        }

        if (start == 0)
        {
            return text;
        }

        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return $"(output cut: the first {start} of {text.Length} chars are left out)\n{text[start..]}";
    }
}
