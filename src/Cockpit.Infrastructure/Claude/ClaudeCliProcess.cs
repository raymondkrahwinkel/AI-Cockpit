using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

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
/// Auth-aware spawn (this increment): when started under a <see cref="ClaudeProfile"/>,
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
    private Process? _process;
    private bool _started;

    public ClaudeCliProcess(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter,
        IPermissionServerState permissionServerState)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
        _permissionServerState = permissionServerState;
    }

    public bool HasExited => _started && (_process?.HasExited ?? true);

    public void Start(ClaudeProfile? profile = null, string? permissionMode = null, string? model = null)
    {
        var cli = _options.Claude;
        var workingDirectory = string.IsNullOrWhiteSpace(cli.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : cli.WorkingDirectory;

        if (profile is not null)
        {
            // Trust must land before the process starts, or the CLI shows its interactive
            // trust dialog with nothing able to answer it headlessly.
            _workspaceTrustWriter.MarkWorkingDirectoryTrusted(profile.ConfigDir, Path.GetFullPath(workingDirectory));
        }

        var executablePath = profile?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        var arguments = BuildArguments(cli, permissionMode, model, _permissionServerState);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (profile is not null)
        {
            // Real user env (HOME/USERPROFILE, PATH, ...) is inherited by default
            // (UseShellExecute=false); only CLAUDE_CONFIG_DIR is overridden here.
            startInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profile.ConfigDir;
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
    internal static List<string> BuildArguments(
        ClaudeCliOptions cli,
        string? permissionMode,
        string? model,
        IPermissionServerState permissionServerState)
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

        arguments.AddRange(cli.ExtraArguments);
        return arguments;
    }

    private Process RequireStartedProcess() =>
        _process ?? throw new InvalidOperationException($"{nameof(ClaudeCliProcess)}.{nameof(Start)} must be called before I/O.");
}
