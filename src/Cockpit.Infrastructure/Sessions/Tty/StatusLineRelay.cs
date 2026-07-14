using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Gets a TTY session's limits out of Claude and into the cockpit, by being its statusline.
/// <para>
/// Claude Code hands its statusline command a JSON blob on stdin, and that blob is the <em>only</em> place the
/// five-hour and weekly allowances are readable: they arrive in response headers the cockpit never sees, and
/// appear in no transcript, no session file and no subcommand. So the cockpit registers a statusline of its own
/// for the sessions it launches (<c>--settings</c>, which merges over the operator's own settings for that
/// process only) and the script writes the blob where the session's header can read it.
/// </para>
/// <para>
/// It then runs the operator's <em>own</em> statusline with the same input and prints what it prints. An operator
/// who built a statusline should not lose it because their sessions now run in the cockpit — a feature that
/// silently takes something away is not a feature.
/// </para>
/// <para>
/// Correlating the blob with the pane that spawned it is done with an environment variable rather than by
/// matching session ids or transcript paths: the statusline command is a child of <c>claude</c>, so it inherits
/// the variable the launcher set, and it knows which file to write without anyone having to guess.
/// </para>
/// </summary>
internal sealed class StatusLineRelay : IStatusLineRelay, ISingletonService
{
    /// <summary>The env var carrying the file this session's statusline JSON is written to. Read by the script below.</summary>
    public const string StatusFileVariable = "COCKPIT_STATUS_FILE";

    private const string ScriptName = "statusline-relay.sh";

    private const string PowerShellScriptName = "statusline-relay.ps1";

    public (string? StatusFile, string? SettingsJson) Install(
        SessionProfile? profile,
        string userProfileDirectory,
        IDictionary<string, string> environment)
    {
        try
        {
            var configDirectory = ClaudeConfigDirectory.ResolveConfigJsonDirectory(profile, userProfileDirectory);
            var settings = SettingsJson(ReadExistingStatusLineCommand(configDirectory));
            if (settings is null)
            {
                return (null, null);
            }

            var statusFile = NewStatusFile();
            environment[StatusFileVariable] = statusFile;

            return (statusFile, settings);
        }
        catch (Exception)
        {
            // A statusline is a nicety. A session that cannot have one still has to start.
            return (null, null);
        }
    }

    /// <summary>A fresh file for one session's statusline snapshots.</summary>
    private static string NewStatusFile() =>
        Path.Combine(StatusDirectory, $"{Guid.NewGuid():N}.json");

    private static string StatusDirectory => Path.Combine(CockpitConfigPath.Root, "statusline");

    /// <summary>The <c>--settings</c> JSON that points Claude's statusline at the relay.</summary>
    internal static string? SettingsJson(string? existingStatusLineCommand)
    {
        var settings = new JsonObject
        {
            ["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = EnsureScript(existingStatusLineCommand),
            },
        };

        return settings.ToJsonString();
    }

    /// <summary>The operator's own statusline command, as configured in the profile's <c>settings.json</c> — the one the relay must keep running.</summary>
    internal static string? ReadExistingStatusLineCommand(string claudeConfigDirectory)
    {
        try
        {
            var path = Path.Combine(claudeConfigDirectory, "settings.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("statusLine", out var statusLine)
                && statusLine.ValueKind == JsonValueKind.Object
                && statusLine.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                    ? command.GetString()
                    : null;
        }
        catch (Exception)
        {
            // A settings file we cannot read is a statusline we cannot chain to. The relay still reports the limits.
            return null;
        }
    }

    /// <summary>
    /// Writes the relay script and returns the command Claude should run. Rewritten on every launch, so a
    /// statusline the operator changed since last time is picked up.
    /// <para>
    /// Two scripts, because the cockpit runs on three platforms and a bash script is not one of the things
    /// Windows has. Same shape either way: keep the JSON, then run whatever statusline was already configured.
    /// </para>
    /// </summary>
    private static string EnsureScript(string? existingStatusLineCommand) =>
        OperatingSystem.IsWindows()
            ? WritePowerShellScript(existingStatusLineCommand)
            : WriteBashScript(existingStatusLineCommand);

    private static string WriteBashScript(string? existingStatusLineCommand)
    {
        Directory.CreateDirectory(StatusDirectory);

        var chain = string.IsNullOrWhiteSpace(existingStatusLineCommand)
            ? string.Empty
            : $"""

               # The operator's own statusline, fed the same JSON, its output passed through as ours.
               printf '%s' "$input" | {existingStatusLineCommand}
               """;

        // No `set -u`: the status file variable is deliberately allowed to be absent (a session launched without
        // the relay), and an unset-variable abort would take the operator's own statusline down with it.
        var script = $"""
            #!/usr/bin/env bash
            # Written by AI-Cockpit. Claude Code pipes its statusline JSON in on stdin; this keeps a copy for the
            # session's header (${StatusFileVariable}) and then runs whatever statusline the operator had.
            set -o pipefail
            umask 077

            input="$(cat)"

            if [ -n "${StatusFileVariable}" ]; then
                # Written whole and moved into place: the cockpit reads this file on a timer, and a half-flushed
                # file would parse as nothing.
                printf '%s' "$input" > "${StatusFileVariable}.tmp" 2>/dev/null && mv -f "${StatusFileVariable}.tmp" "${StatusFileVariable}" 2>/dev/null
            fi
            {chain}
            """;

        var path = Path.Combine(CockpitConfigPath.Root, ScriptName);
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    private static string WritePowerShellScript(string? existingStatusLineCommand)
    {
        Directory.CreateDirectory(StatusDirectory);

        var chain = string.IsNullOrWhiteSpace(existingStatusLineCommand)
            ? string.Empty
            : $"""

               # The operator's own statusline, fed the same JSON, its output passed through as ours. Run through
               # cmd, since what they configured is a command line, not necessarily a PowerShell cmdlet.
               $payload | & cmd /c "{existingStatusLineCommand}"
               """;

        var script = $$"""
            # Written by AI-Cockpit. Claude Code pipes its statusline JSON in on stdin; this keeps a copy for the
            # session's header ({{StatusFileVariable}}) and then runs whatever statusline the operator had.
            $ErrorActionPreference = 'SilentlyContinue'
            $payload = [Console]::In.ReadToEnd()

            $target = $env:{{StatusFileVariable}}
            if ($target) {
                # Written whole and moved into place: the cockpit reads this file on a timer, and a half-flushed
                # file would parse as nothing.
                [System.IO.File]::WriteAllText("$target.tmp", $payload)
                Move-Item -Force -LiteralPath "$target.tmp" -Destination $target
            }
            {{chain}}
            """;

        var path = Path.Combine(CockpitConfigPath.Root, PowerShellScriptName);
        File.WriteAllText(path, script);

        // Quoted: the config directory is under the operator's profile, and Windows user names have spaces in them
        // far more often than anyone remembers when they write the path unquoted.
        return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{path}\"";
    }

    /// <summary>Removes a session's snapshot file. The limits of a session that ended are nobody's business.</summary>
    public static void Delete(string statusFile)
    {
        try
        {
            File.Delete(statusFile);
        }
        catch (Exception)
        {
            // Swept on the next start.
        }
    }

    /// <summary>Clears the snapshots left behind by sessions that were killed rather than closed.</summary>
    public static void SweepStale()
    {
        try
        {
            if (Directory.Exists(StatusDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(StatusDirectory, "*.json"))
                {
                    Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping never fails a launch.
        }
    }
}
