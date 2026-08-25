using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Shell;

// The `cockpit-shell` MCP tool (AC-1066): the shell a session with none of its own otherwise lacks. No command
// allow-list on purpose — `dotnet`/`python3`/`npx` all run arbitrary code, so one would only look like a boundary.
// `run_command` is annotated Destructive instead; the existing AC-79 ceiling machinery decides each call.
internal sealed class ShellMcpTools(IShellCommandRunner runner)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // Same ceiling as TerminalMcpTools.RunInTerminal: long enough for a real build, capped so a call cannot park
    // the session indefinitely.
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 600;

    [McpServerTool(Name = "run_command", ReadOnly = false, Destructive = true)]
    [Description("Runs one command as a child process and waits for it to finish — the shell a session on a provider with no built-in one (Bash is a Claude Code CLI feature) otherwise lacks. Returns exitCode, stdout, stderr, and timedOut. The command and its arguments are never re-parsed by a shell — pass each argument separately in `arguments`; to run a pipeline or use shell syntax, name a shell (e.g. \"sh\") as `command` and pass \"-c\" plus the script as arguments yourself. A run past `timeoutSeconds` is killed (including any child processes it started) and still returns whatever it had already printed, with timedOut set.")]
    public async Task<string> RunCommand(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The directory to run the command in. Must already exist.")] string directory,
        [Description("The executable to run, e.g. \"git\" or \"dotnet\" — never a shell string.")] string command,
        [Description("The command's arguments, one per array entry — never re-parsed by a shell.")] IReadOnlyList<string>? arguments = null,
        [Description("How long to wait, in seconds. Default 30, capped at 600.")] int timeoutSeconds = DefaultTimeoutSeconds)
    {
        if (!Directory.Exists(directory))
        {
            return _Serialize(new { ok = false, error = $"No such directory: {directory}" });
        }

        // Same fallback as every other cockpit tool (AC-89): a transport-verified pane wins, `session` only
        // stands in when there is none — the in-process host tool loop that runs the non-Claude providers this
        // tool exists for.
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds));
        var result = await runner.RunAsync(directory, command, arguments ?? [], timeout).ConfigureAwait(false);
        return _Serialize(new
        {
            ok = true,
            session = caller,
            exitCode = result.ExitCode,
            timedOut = result.TimedOut,
            durationSeconds = Math.Round(result.Duration.TotalSeconds, 1),
            stdout = result.StandardOutput,
            stderr = result.StandardError,
        });
    }

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
