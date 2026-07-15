using System.Text.Json.Nodes;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Gets a Claude TTY session's limits out of Claude and into the cockpit by being its statusline — a copy of the
/// host's <c>StatusLineRelay</c> (weg A). Claude's five-hour/weekly allowances are readable <em>only</em> in the
/// JSON it hands its statusline command on stdin, so this registers a statusline of its own (via <c>--settings</c>,
/// merged over the operator's own for this process only) whose script keeps the JSON where the session header can
/// read it and then runs whatever statusline the operator already had — a feature that silently took the operator's
/// statusline away would be no feature. The snapshot file is named in the launch spec's StatusFile (the host polls
/// it) and in its SessionScopedFiles (the host deletes it when the session ends).
/// </summary>
internal static class ClaudeStatusLine
{
    /// <summary>The env var carrying the file this session's statusline JSON is written to. Read by the script below.</summary>
    public const string StatusFileVariable = "COCKPIT_STATUS_FILE";

    private const string ScriptName = "statusline-relay.sh";
    private const string PowerShellScriptName = "statusline-relay.ps1";

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit", "claude-provider");

    private static string StatusDirectory => Path.Combine(Root, "statusline");

    /// <summary>
    /// Installs the relay for one session: writes the script, computes the <c>--settings</c> JSON, names a fresh
    /// snapshot file and points the env var at it. Returns nulls when there is nothing to chain and no settings to
    /// write — a statusline is a nicety, and a session that cannot have one still starts.
    /// </summary>
    public static (string? StatusFile, string? SettingsJson) Install(string configJsonDirectory, IDictionary<string, string?> environment)
    {
        try
        {
            var settings = SettingsJson(ReadExistingStatusLineCommand(configJsonDirectory));
            if (settings is null)
            {
                return (null, null);
            }

            var statusFile = Path.Combine(StatusDirectory, $"{Guid.NewGuid():N}.json");
            environment[StatusFileVariable] = statusFile;
            return (statusFile, settings);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? SettingsJson(string? existingStatusLineCommand)
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

    private static string? ReadExistingStatusLineCommand(string claudeConfigDirectory)
    {
        try
        {
            var path = Path.Combine(claudeConfigDirectory, "settings.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("statusLine", out var statusLine)
                && statusLine.ValueKind == System.Text.Json.JsonValueKind.Object
                && statusLine.TryGetProperty("command", out var command)
                && command.ValueKind == System.Text.Json.JsonValueKind.String
                    ? command.GetString()
                    : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string EnsureScript(string? existingStatusLineCommand) =>
        OperatingSystem.IsWindows()
            ? WritePowerShellScript(existingStatusLineCommand)
            : WriteBashScript(existingStatusLineCommand);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string WriteBashScript(string? existingStatusLineCommand)
    {
        Directory.CreateDirectory(StatusDirectory);

        var chain = string.IsNullOrWhiteSpace(existingStatusLineCommand)
            ? string.Empty
            : $"""

               # The operator's own statusline, fed the same JSON, its output passed through as ours.
               printf '%s' "$input" | {existingStatusLineCommand}
               """;

        var script = $"""
            #!/usr/bin/env bash
            # Written by AI-Cockpit (Claude provider plugin). Claude Code pipes its statusline JSON in on stdin; this
            # keeps a copy for the session's header (${StatusFileVariable}) and then runs the operator's own statusline.
            set -o pipefail
            umask 077

            input="$(cat)"

            if [ -n "${StatusFileVariable}" ]; then
                printf '%s' "$input" > "${StatusFileVariable}.tmp" 2>/dev/null && mv -f "${StatusFileVariable}.tmp" "${StatusFileVariable}" 2>/dev/null
            fi
            {chain}
            """;

        var path = Path.Combine(Root, ScriptName);
        Directory.CreateDirectory(Root);
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

               # The operator's own statusline, fed the same JSON, its output passed through as ours.
               $payload | & cmd /c "{existingStatusLineCommand}"
               """;

        var script = $$"""
            # Written by AI-Cockpit (Claude provider plugin). Claude Code pipes its statusline JSON in on stdin; this
            # keeps a copy for the session's header ({{StatusFileVariable}}) and then runs the operator's own statusline.
            $ErrorActionPreference = 'SilentlyContinue'
            $payload = [Console]::In.ReadToEnd()

            $target = $env:{{StatusFileVariable}}
            if ($target) {
                [System.IO.File]::WriteAllText("$target.tmp", $payload)
                Move-Item -Force -LiteralPath "$target.tmp" -Destination $target
            }
            {{chain}}
            """;

        var path = Path.Combine(Root, PowerShellScriptName);
        Directory.CreateDirectory(Root);
        File.WriteAllText(path, script);
        return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{path}\"";
    }
}
