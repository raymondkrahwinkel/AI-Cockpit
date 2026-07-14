using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Core.Configuration;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// The <c>claude</c> CLI as a TTY provider: resolves the executable, pre-marks the working directory trusted,
/// composes the CLI's flags, and writes the session-scoped files it needs (the shared MCP registry, the
/// statusline relay that is the only machine-readable route to Claude's limits).
/// <para>
/// Everything Claude-specific about a TTY session lives here. What is left in <see cref="TtyLauncher"/> — the
/// pty, the base environment, the cleanup — is what every TUI needs identically. Unlike SDK mode this never
/// adds <c>-p</c>/stream-json flags, so the genuine interactive TUI runs; once running, the TUI itself owns any
/// live switching (<c>/model</c>, Shift+Tab), since TTY mode has no control channel.
/// </para>
/// </summary>
internal sealed class ClaudeTtySessionProvider : ITtySessionProvider, ISingletonService
{
    /// <summary>Stable id, also the key the New-session dialog stores on a session. Not a display name.</summary>
    public const string Id = "claude";

    private readonly CockpitOptions _options;
    private readonly IClaudeExecutableLocator _executableLocator;
    private readonly WorkspaceTrustWriter _workspaceTrustWriter;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly IStatusLineRelay? _statusLineRelay;

    public ClaudeTtySessionProvider(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter,
        IMcpServerStore mcpServerStore,
        // Optional so a unit test can build a launch without one: installing the relay writes a script and a
        // snapshot file, and a test of the argument building has no business leaving anything in the operator's
        // config directory. Absent, the session simply reports no limits.
        IStatusLineRelay? statusLineRelay = null)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
        _mcpServerStore = mcpServerStore;
        _statusLineRelay = statusLineRelay;
    }

    public string ProviderId => Id;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var cli = _options.Claude;

        // The launcher resolved the per-session/global/process-cwd precedence, except for the one step it cannot
        // know about: Claude's own configured working directory sits between them.
        var workingDirectory = string.IsNullOrWhiteSpace(cli.WorkingDirectory)
            ? context.WorkingDirectory
            : Path.GetFullPath(cli.WorkingDirectory);

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (context.Profile is not null)
        {
            // Same rule as SDK mode: trust must land before the process starts, or the TUI blocks on its
            // interactive trust dialog on first render. It must land in the .claude.json the CLI actually reads
            // for this spawn — the profile dir for a non-default profile, the home root for a default-dir profile
            // (whose CLAUDE_CONFIG_DIR stays unset).
            _workspaceTrustWriter.MarkWorkingDirectoryTrusted(
                ClaudeConfigDirectory.ResolveConfigJsonDirectory(context.Profile.Claude, userHome),
                workingDirectory);
        }

        var executablePath = context.Profile?.Claude?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        var environmentOverlay = new Dictionary<string, string?>(
            ClaudeTtyEnvironment.BuildOverlay(context.BaseEnvironment, context.Profile, userHome),
            StringComparer.OrdinalIgnoreCase);

        var mcpConfigPath = _WriteRegistryMcpConfig();

        // Claude's own limits — how full the context window is, and how much of the five-hour and weekly
        // allowance is gone — reach the cockpit only through the statusline it hands its JSON to. The session
        // gets a statusline of ours (which still runs the operator's own) and a file of its own to write to; the
        // env var is how the script, a grandchild of this process, knows which file is its.
        var (statusFile, statusLineSettings) = _statusLineRelay?.Install(context.Profile, userHome, environmentOverlay) ?? (null, null);

        var arguments = BuildArguments(
            context.Options.GetValueOrDefault(TtyLaunchOption.PermissionMode),
            context.Options.GetValueOrDefault(TtyLaunchOption.Model),
            context.Options.GetValueOrDefault(TtyLaunchOption.Effort),
            mcpConfigPath,
            _CanDelegate(),
            context.Resume,
            statusLineSettings);

        return new TtyLaunchSpec(
            executablePath,
            arguments,
            environmentOverlay,
            workingDirectory,
            mcpConfigPath is null ? [] : [mcpConfigPath],
            statusFile);
    }

    /// <summary>True when the operator enabled the orchestrator (#67), so this TUI session may hand work to another profile.</summary>
    private bool _CanDelegate()
    {
        try
        {
            var registry = _mcpServerStore.LoadAsync().GetAwaiter().GetResult();
            return registry.Any(server =>
                server.Enabled && string.Equals(server.Name, DelegationMcp.ServerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Fans the shared MCP registry (#26) into the interactive TUI by writing a registry-only
    /// <c>--mcp-config</c> file (no cockpit permission server — the TUI prompts for permission itself) and
    /// returning its path, or <see langword="null"/> when the registry has no Claude-eligible server. Sync
    /// (matching the synchronous spawn path) and best-effort: any failure just launches the session without the
    /// shared servers rather than blocking it.
    /// </summary>
    private string? _WriteRegistryMcpConfig()
    {
        try
        {
            var registry = _mcpServerStore.LoadAsync().GetAwaiter().GetResult();
            var json = McpConfigFile.SerializeRegistryOnly(registry);

            return json is null ? null : TtyMcpConfigFile.Write(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the launch-only start-default flags for the TTY spawn. Extracted (and <c>internal</c>) so the flag
    /// construction is unit-testable without a real pty. Deliberately narrower than
    /// <c>ClaudeCliProcess.BuildArguments</c> — no <c>-p</c>/stream-json/permission-prompt-tool wiring, since TTY
    /// mode runs the genuine interactive TUI (it prompts for permission itself). The session id is deliberately
    /// <em>not</em> forced (<c>--session-id</c> is undocumented for a new interactive session and does not persist
    /// a transcript under that id) — the cockpit instead locates the live transcript as the new file that appears
    /// after launch (see <c>ISessionTranscriptReader</c>).
    /// </summary>
    internal static List<string> BuildArguments(
        string? permissionMode,
        string? model,
        string? effort,
        string? mcpConfigPath = null,
        bool canDelegate = false,
        SessionResume? resume = null,
        string? settingsJson = null)
    {
        var arguments = new List<string>();

        // Settings for this process only — the statusline relay (StatusLineRelay). Passed as JSON rather than a
        // file so it never lands on disk to be forgotten, and merged by the CLI over the operator's own settings,
        // which stay untouched.
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            arguments.Add("--settings");
            arguments.Add(settingsJson);
        }

        // Pick up an earlier conversation rather than starting cold — the same two CLI flags the SDK spawn uses.
        // --resume without an id would open the CLI's own interactive picker, which the cockpit does not want:
        // the choice was already made in the New-session dialog.
        if (resume is { Mode: SessionResumeMode.MostRecent })
        {
            arguments.Add("--continue");
        }
        else if (resume is { Mode: SessionResumeMode.BySessionId, SessionId: { } sessionId } && !string.IsNullOrWhiteSpace(sessionId))
        {
            arguments.Add("--resume");
            arguments.Add(sessionId.Trim());
        }

        // Bypass is a launch-only synonym for --dangerously-skip-permissions; the CLI does not accept both flags
        // together, so the two are mutually exclusive here (mirrors ClaudeCliProcess's bypass handling, which
        // likewise skips --permission-mode in that case).
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

        // Fan the shared MCP registry (#26) into the interactive TUI. Deliberately without --strict-mcp-config:
        // --mcp-config adds the cockpit-configured servers on top of the CLI's own user/project config, so the
        // TTY session gains the registry's servers without losing the user's own .mcp.json (strict would replace
        // them). No permission server here — the TUI handles permission prompts itself.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfigPath);
        }

        // Same nudge the SDK spawn gets (#67): the orchestrator's tools are only reached for if the model knows
        // when they are worth reaching for.
        if (canDelegate)
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(DelegationSystemPrompt.Default);
        }

        return arguments;
    }
}
