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

        // AC-408: the session id is not forced on the launch (see BuildArguments' remark), so it is derived the
        // same way ClaudeTranscriptReader already does for read-aloud/status — as the new *.jsonl transcript that
        // appears under this config dir after launch. Snapshotting before returning captures "known before this
        // session" so the background watch below only ever reports transcripts this session itself created.
        if (context.ReportConversationId is { } reportConversationId)
        {
            var stateDirectory = ClaudeConfigPaths.ResolveStateDirectory(
                config.ConfigDir, Environment.GetEnvironmentVariable(ClaudeConfigPaths.EnvironmentVariable), userHome);
            var knownAtLaunch = new ClaudeTranscriptReader().SnapshotTranscripts(context.ConfigJson);
            _ = WatchConversationIdAsync(stateDirectory, knownAtLaunch, reportConversationId);
        }

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
            // The standing instructions the cockpit resolved for this session (a profile's identity, a project's
            // behaviour) alongside the orchestrator nudge: both are things the model should start knowing, and the
            // CLI takes one --append-system-prompt, so they travel as one value.
            _AppendedInstructions(context.Options.GetValueOrDefault(WellKnownPluginSessionOptions.AppendSystemPrompt), context.DelegationSystemPrompt),
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
    /// How often <see cref="WatchConversationIdAsync"/> re-scans the config dir — the same interval
    /// <see cref="ClaudeTranscriptReader"/> already polls at.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long <see cref="WatchConversationIdAsync"/> keeps scanning before giving up and reporting nothing —
    /// long enough to outlast ordinary CLI startup latency (process spawn, an auth/version check) before the
    /// transcript file exists at all, short enough to close the cross-session window described there promptly
    /// rather than leaving it open for the rest of the app's life.
    /// </summary>
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reports this session's conversation id exactly once, as soon as its own transcript can be told apart from
    /// every other file under <paramref name="stateDirectory"/> (AC-408) — the same "new file since launch"
    /// identification <see cref="ClaudeTranscriptReader"/> uses for read-aloud/status, but a bounded one-shot scan
    /// rather than a standing watch.
    /// <para>
    /// A standing watch would keep scanning the <em>whole</em> config dir — every session's transcripts, not just
    /// this one's — for as long as this session runs: Claude's per-session <c>&lt;cwd-hash&gt;</c> folder name is
    /// undocumented, so narrowing the scan to just this session's own folder was rejected (see the remark on
    /// <see cref="BuildArguments"/>), and forcing <c>--session-id</c> is rejected for the same reason. Left
    /// unbounded, a second session starting under the same config dir later in this session's life would
    /// eventually be seen and misreported as <em>this</em> session's id — silently, and the wrong pane would be
    /// resumed into the wrong conversation once a later ticket persists what this one reports. Stopping after one
    /// report, or after <see cref="WatchTimeout"/> with nothing found, closes that window instead of leaving it
    /// open.
    /// </para>
    /// <para>
    /// If more than one new file shows up in the very same poll, this session's own transcript cannot be told
    /// apart from another session's that just started in the same instant — reporting nothing is the correct
    /// answer there, not guessing the newest one (a wrong <see cref="PluginConversationIdState.Known"/> is worse
    /// than none at all).
    /// </para>
    /// <para>
    /// Consequence, deliberately accepted: this route never reports a changed id after a <c>/clear</c> — the new
    /// transcript it starts is exactly the same "unattributable new file" case above, and this scan cannot tell
    /// it apart from another session starting. <see cref="PluginTtyLaunchContext.ReportConversationId"/> itself
    /// still allows repeated calls, and the SDK route (<see cref="IPluginSessionDriver.Conversation"/>) does
    /// report a live mid-session change; this TTY route only cannot do so reliably, and does not pretend it can.
    /// </para>
    /// </summary>
    internal static async Task WatchConversationIdAsync(
        string stateDirectory,
        IReadOnlySet<string> knownAtLaunch,
        Action<PluginConversationId> reportConversationId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null)
    {
        var interval = pollInterval ?? PollInterval;
        var deadline = DateTime.UtcNow + (timeout ?? WatchTimeout);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var newTranscripts = ClaudeTranscriptReader.EnumerateTranscripts(stateDirectory).Where(path => !knownAtLaunch.Contains(path)).ToList();
                if (newTranscripts.Count == 1)
                {
                    reportConversationId(PluginConversationId.Known(Path.GetFileNameWithoutExtension(newTranscripts[0])));
                    return;
                }

                if (newTranscripts.Count > 1)
                {
                    return;
                }

                await Task.Delay(interval, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best-effort — a transient enumeration failure (the config dir vanishing) simply ends the watch
            // instead of reporting a conversation id that was never confirmed.
        }
    }

    /// <summary>
    /// The session's standing instructions and the orchestrator nudge as one value, blank-separated — the
    /// instructions first, since they say who the session is and what it works on, and the nudge is a note about
    /// tools. Null when neither applies, which leaves the flag off entirely.
    /// </summary>
    internal static string? _AppendedInstructions(string? instructions, string? delegationSystemPrompt)
    {
        var parts = new[] { instructions, delegationSystemPrompt }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
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
        string? appendSystemPrompt,
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

        // What the session starts knowing: the standing instructions a profile/project gave it (AC-142/AC-158) and
        // the orchestrator nudge (#67), whose tools are only reached for if the model knows when they are worth it.
        if (!string.IsNullOrWhiteSpace(appendSystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(appendSystemPrompt);
        }

        return arguments;
    }
}
