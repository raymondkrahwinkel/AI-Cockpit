using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Real <see cref="IClaudeCliProcess"/> backed by a spawned <c>claude</c> process running in
/// persistent, multi-turn headless mode:
/// <c>claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages</c>.
/// Grounded in https://code.claude.com/docs/en/headless.md ("Stream responses": stream-json
/// output requires --verbose; --include-partial-messages adds token-level deltas) and
/// https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md (streaming input mode:
/// a persistent process fed one JSON user-message object per stdin line keeps a single
/// multi-turn session alive).
/// </summary>
/// <remarks>
/// F-C1 caveat: this sandbox has no logged-in <c>claude</c> CLI, so this class has never been
/// exercised against a real process here. It is deliberately kept as a thin, mockable seam
/// (<see cref="IClaudeCliProcess"/>) so <c>ClaudeCliSession</c>'s turn-taking logic is unit
/// tested against a fake; the live end-to-end run requires Raymond's logged-in environment.
///
/// Auth-aware spawn (this increment): when started under a <see cref="SessionProfile"/>,
/// <c>CLAUDE_CONFIG_DIR</c> is set to the profile's config directory so the spawned CLI reads
/// that profile's own login/config — the real process environment (HOME/USERPROFILE, PATH,
/// etc.) is otherwise inherited as-is. No <c>--bare</c> (would skip reading the token) and no
/// <c>ANTHROPIC_API_KEY</c> is ever set (that would switch the CLI to API-key billing instead
/// of the subscription route — see Decisions.md auth section).
/// </remarks>
internal sealed class ClaudeCliProcess : IClaudeCliProcess
{
    private readonly CockpitOptions _options;
    private readonly IClaudeExecutableLocator _executableLocator;
    private readonly WorkspaceTrustWriter _workspaceTrustWriter;
    private readonly IPermissionServerState _permissionServerState;
    private readonly IMcpServerStore _mcpServerStore;
    private Process? _process;
    private bool _started;

    public ClaudeCliProcess(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter,
        IPermissionServerState permissionServerState,
        IMcpServerStore mcpServerStore)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
        _permissionServerState = permissionServerState;
        _mcpServerStore = mcpServerStore;
    }

    public bool HasExited => _started && (_process?.HasExited ?? true);

    public void Start(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectoryOverride = null)
    {
        var cli = _options.Claude;
        // A per-session override (from the New-session dialog) wins over the global option, which in turn wins
        // over the process cwd.
        var workingDirectory = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride
            : string.IsNullOrWhiteSpace(cli.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : cli.WorkingDirectory;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (profile is not null)
        {
            // Trust must land before the process starts, or the CLI shows its interactive trust dialog with
            // nothing able to answer it headlessly. It must land in the .claude.json the CLI actually reads
            // for this spawn — the profile dir for a non-default profile, the home root for a default-dir
            // profile (whose CLAUDE_CONFIG_DIR stays unset).
            _workspaceTrustWriter.MarkWorkingDirectoryTrusted(
                ClaudeConfigDirectory.ResolveConfigJsonDirectory(profile, userHome),
                Path.GetFullPath(workingDirectory));
        }

        var executablePath = profile?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        // Fan the shared MCP registry out to this spawn: rewrite the --mcp-config the CLI is about to read so
        // it carries the same servers the local-LLM tool-loop hosts, alongside the permission server. Done
        // per spawn so registry edits take effect for the next session without an app restart. The per-session
        // selection (#44) narrows which registry servers are included for this particular spawn.
        var canDelegate = FanOutRegistryToMcpConfig(permissionMode, enabledMcpServerNames);

        var arguments = BuildArguments(cli, permissionMode, model, _permissionServerState, canDelegate);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // claude speaks UTF-8 (→, ✅, emoji in tool output); without pinning the redirected streams
            // to UTF-8 .NET decodes them with the OS default code page (ANSI/OEM on Windows) and mangles
            // them (mojibake — bug #23). BOM-less, so the input side stays a clean JSON byte stream.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (profile is not null)
        {
            // Real user env (HOME/USERPROFILE, PATH, ...) is inherited by default (UseShellExecute=false).
            // A non-default profile dir is exported as CLAUDE_CONFIG_DIR; a default-dir profile clears any
            // inherited value so the CLI uses its native home-root config/login (setting it to ~/.claude is
            // not a no-op — see ClaudeConfigDirectory.ResolveSpawnOverride).
            var configDirOverride = ClaudeConfigDirectory.ResolveSpawnOverride(profile, userHome);
            if (configDirOverride is not null)
            {
                startInfo.EnvironmentVariables[ClaudeConfigDirectory.EnvironmentVariable] = configDirOverride;
            }
            else
            {
                startInfo.EnvironmentVariables.Remove(ClaudeConfigDirectory.EnvironmentVariable);
            }
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Start();
        _started = true;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var process = RequireStartedProcess();
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var process = RequireStartedProcess();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && _process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Close/Kill.
            }
        }

        _process?.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Builds the CLI argument list for a spawn. Extracted (and <c>internal</c>) so the flag
    /// construction — especially the permission-prompt/MCP wiring — is unit-testable without
    /// spawning a real process.
    /// </summary>
    /// <param name="canDelegate">
    /// True when this session actually gets the orchestrator's tools (#67). The tool descriptions say what they
    /// do, but nothing about when reaching for them is worthwhile — without a nudge, an agent that could hand
    /// bulk work to a local model just does it itself and the tools go unused.
    /// </param>
    internal static List<string> BuildArguments(
        ClaudeCliOptions cli,
        string? permissionMode,
        string? model,
        IPermissionServerState permissionServerState,
        bool canDelegate = false)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(permissionMode) ? cli.PermissionMode : permissionMode;

        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-mode", effectiveMode,
        };

        // Route real permission enforcement through the cockpit's shared MCP server: the CLI calls
        // our permission_prompt tool for any tool that genuinely needs approval, and the operator's
        // allow/deny flows back in-band. --strict-mcp-config keeps the CLI from also loading the
        // user's own MCP servers, so the only tool it sees is ours.
        //
        // bypassPermissions allows every tool with no prompt, so wiring the prompt tool there is
        // pointless and re-introduces the very prompts the operator asked to bypass — skip it so
        // bypass actually bypasses (bug #15). Other modes (acceptEdits included, which only
        // auto-accepts edits and still gates Bash et al.) keep the prompt tool.
        var bypassesPermissions = string.Equals(effectiveMode, "bypassPermissions", StringComparison.Ordinal);

        if (!bypassesPermissions && permissionServerState is { McpConfigPath: { } configPath, PermissionPromptToolName: { } toolName })
        {
            arguments.Add("--permission-prompt-tool");
            arguments.Add(toolName);
            arguments.Add("--mcp-config");
            arguments.Add(configPath);
            arguments.Add("--strict-mcp-config");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        if (canDelegate)
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(DelegationSystemPrompt.Default);
        }

        arguments.AddRange(cli.ExtraArguments);
        return arguments;
    }

    // Rewrites the --mcp-config file (the one BuildArguments points --mcp-config at) with the permission
    // server plus the current shared registry, so a Claude session sees the same MCP servers as a local-LLM
    // session (#26 fan-out) — narrowed to enabledMcpServerNames when the New-session dialog made a per-session
    // selection (#44). Skipped in bypass mode (no --mcp-config is passed there) and when the permission
    // server isn't ready yet. Best-effort: any failure leaves the baseline permission-only config in place
    // rather than blocking the spawn.
    /// <returns>True when the orchestrator's tools (#67) end up in this session's config, so the caller can tell the model it may delegate.</returns>
    private bool FanOutRegistryToMcpConfig(string? permissionMode, IReadOnlySet<string>? enabledMcpServerNames)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(permissionMode) ? _options.Claude.PermissionMode : permissionMode;
        if (string.Equals(effectiveMode, "bypassPermissions", StringComparison.Ordinal))
        {
            return false;
        }

        if (_permissionServerState is not { McpConfigPath: { } configPath, PermissionMcpUrl: { } mcpUrl })
        {
            return false;
        }

        try
        {
            // Sync-over-async is deliberate: Start is a synchronous spawn path (it already writes the trust
            // file inline), and the store is a small local cockpit.json read that never touches the UI thread.
            var registry = _mcpServerStore.LoadAsync().GetAwaiter().GetResult();
            var sessionRegistry = McpServerRegistryFilter.ApplySessionSelection(registry, enabledMcpServerNames);
            File.WriteAllText(configPath, McpConfigFile.Serialize(mcpUrl, sessionRegistry));

            return sessionRegistry.Any(server =>
                server.Enabled && string.Equals(server.Name, DelegationMcp.ServerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Leave the baseline config the permission server wrote; the session still gets permission gating.
            return false;
        }
    }

    private Process RequireStartedProcess() =>
        _process ?? throw new InvalidOperationException($"{nameof(ClaudeCliProcess)}.{nameof(Start)} must be called before I/O.");
}
