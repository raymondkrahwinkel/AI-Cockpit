using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The <c>claude</c> CLI as a TTY provider, hosted in the plugin (Fase 4, weg A) — a port of the host's
/// <c>ClaudeTtySessionProvider</c>: resolves the executable, pre-marks the working directory trusted, installs the
/// statusline relay that carries Claude's limits, fans the shared MCP registry into a <c>--mcp-config</c>, and
/// composes the launch-only flags. Never adds <c>-p</c>/stream-json — this is the genuine interactive TUI, which
/// owns its own live switching (<c>/model</c>, Shift+Tab) since TTY mode has no control channel.
/// </summary>
internal sealed class ClaudeTtyProvider(Func<string, string?>? managedResolver = null) : IPluginTtyProvider
{
    public const string PermissionModeKey = "permission-mode";
    public const string ModelKey = "model";
    public const string EffortKey = "effort";

    public PluginTtyLaunchSpec BuildLaunch(PluginTtyLaunchContext context)
    {
        var config = ClaudeProviderConfig.Parse(context.ConfigJson);
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var workingDirectory = context.WorkingDirectory;
        var configJsonDirectory = ClaudeConfigPaths.ResolveConfigJsonDirectory(config.ConfigDir, userHome);

        // Trust must land before the process starts, or the TUI blocks on its interactive trust dialog on first
        // render — in the .claude.json the CLI reads for this spawn (the profile dir for a non-default profile).
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(configJsonDirectory, workingDirectory);

        var environmentOverlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (ClaudeConfigPaths.ResolveSpawnOverride(config.ConfigDir, userHome) is { } configDirOverride)
        {
            environmentOverlay[ClaudeConfigPaths.EnvironmentVariable] = configDirOverride;
        }

        var mcpConfigPath = ClaudeMcpConfig.Write(context.McpServers);
        var (statusFile, statusLineSettings) = ClaudeStatusLine.Install(configJsonDirectory, environmentOverlay);

        var arguments = BuildArguments(
            context.Options.GetValueOrDefault(PermissionModeKey),
            context.Options.GetValueOrDefault(ModelKey),
            context.Options.GetValueOrDefault(EffortKey),
            mcpConfigPath,
            context.DelegationSystemPrompt,
            context.Resume,
            statusLineSettings);

        var sessionScopedFiles = new List<string>(2);
        if (mcpConfigPath is not null)
        {
            sessionScopedFiles.Add(mcpConfigPath);
        }

        if (statusFile is not null)
        {
            sessionScopedFiles.Add(statusFile);
        }

        return new PluginTtyLaunchSpec(
            // Resolve against PATH like the SDK route does: a bare "claude" is not spawnable directly on Windows
            // (Process does no PATHEXT lookup), so the locator finds the .cmd/.exe/.bat npm shim. A pinned absolute
            // path passes through unchanged. Without this a default (blank-executable) Windows profile fails to start.
            // A cockpit-managed install (AC-20), if present, is preferred over PATH.
            ClaudeExecutableLocator.Resolve(config.ExecutablePath is { Length: > 0 } executable ? executable : "claude", managedResolver),
            arguments,
            environmentOverlay,
            workingDirectory,
            sessionScopedFiles)
        {
            StatusFile = statusFile,
        };
    }

    /// <summary>
    /// The launch-only start-default flags for the TTY spawn (<c>internal</c> for unit tests). Deliberately no
    /// <c>-p</c>/stream-json/permission-prompt-tool: the interactive TUI prompts for permission itself. The session
    /// id is not forced (<c>--session-id</c> is undocumented for a new interactive session); the cockpit locates the
    /// live transcript as the new file that appears after launch.
    /// </summary>
    internal static List<string> BuildArguments(
        string? permissionMode,
        string? model,
        string? effort,
        string? mcpConfigPath,
        string? delegationSystemPrompt,
        PluginTtyResume? resume,
        string? settingsJson)
    {
        var arguments = new List<string>();

        // Settings for this process only — the statusline relay. Passed as JSON, not a file, so it never lands on
        // disk to be forgotten, and merged by the CLI over the operator's own settings, which stay untouched.
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            arguments.Add("--settings");
            arguments.Add(settingsJson);
        }

        // Pick up an earlier conversation. --resume without an id would open the CLI's own picker, which the
        // cockpit does not want — the choice was already made in the New-session dialog.
        if (resume is { SessionId: null })
        {
            arguments.Add("--continue");
        }
        else if (resume is { SessionId: { Length: > 0 } sessionId })
        {
            arguments.Add("--resume");
            arguments.Add(sessionId.Trim());
        }

        // Bypass is a launch-only synonym for --dangerously-skip-permissions; the CLI does not accept both flags,
        // so they are mutually exclusive here.
        if (string.Equals(permissionMode, "bypassPermissions", StringComparison.Ordinal))
        {
            arguments.Add("--dangerously-skip-permissions");
        }
        else if (!string.IsNullOrWhiteSpace(permissionMode))
        {
            arguments.Add("--permission-mode");
            arguments.Add(permissionMode);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            arguments.Add("--effort");
            arguments.Add(effort);
        }

        // Fan the shared MCP registry into the interactive TUI — deliberately without --strict-mcp-config, so the
        // cockpit servers add on top of the CLI's own user/project config rather than replacing it.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfigPath);
        }

        // The orchestrator nudge (#67): its tools are only reached for if the model knows when they are worth it.
        if (!string.IsNullOrWhiteSpace(delegationSystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(delegationSystemPrompt);
        }

        return arguments;
    }
}
