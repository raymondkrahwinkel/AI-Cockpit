using System.Collections;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Claude.Tty;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude.Tty;

/// <summary>
/// Default <see cref="IClaudeTtyLauncher"/>: spawns the interactive <c>claude</c> TUI inside a
/// pseudo console/pty, reusing the SDK-mode profile/executable/trust plumbing. Platform-agnostic —
/// the actual pty host (ConPTY on Windows, Porta.Pty on Linux/macOS) is <see cref="IPtyHostFactory"/>.
/// </summary>
internal sealed class ClaudeTtyLauncher : IClaudeTtyLauncher, ISingletonService
{
    private readonly CockpitOptions _options;
    private readonly IClaudeExecutableLocator _executableLocator;
    private readonly WorkspaceTrustWriter _workspaceTrustWriter;
    private readonly IPtyHostFactory _ptyHostFactory;
    private readonly IMcpServerStore _mcpServerStore;

    public ClaudeTtyLauncher(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter,
        IPtyHostFactory ptyHostFactory,
        IMcpServerStore mcpServerStore)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
        _ptyHostFactory = ptyHostFactory;
        _mcpServerStore = mcpServerStore;
    }

    public IConPtyProcess Launch(
        ClaudeProfile? profile,
        string? permissionMode,
        string? model,
        string? effort,
        short columns,
        short rows,
        string? workingDirectory = null)
    {
        var cli = _options.Claude;
        // Per-session override (New-session dialog) wins over the global option, which wins over the process cwd.
        var resolvedWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
            ? workingDirectory
            : string.IsNullOrWhiteSpace(cli.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : cli.WorkingDirectory;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (profile is not null)
        {
            // Same rule as SDK mode: trust must land before the process starts, or the TUI blocks on its
            // interactive trust dialog on first render. It must land in the .claude.json the CLI actually
            // reads for this spawn — the profile dir for a non-default profile, the home root for a
            // default-dir profile (whose CLAUDE_CONFIG_DIR stays unset).
            _workspaceTrustWriter.MarkWorkingDirectoryTrusted(
                ClaudeConfigDirectory.ResolveConfigJsonDirectory(profile, userHome),
                Path.GetFullPath(resolvedWorkingDirectory));
        }

        var executablePath = profile?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        var environment = TtyEnvironment.Build(CurrentProcessEnvironment(), profile, userHome);
        var arguments = BuildArguments(permissionMode, model, effort, _WriteRegistryMcpConfig());

        return _ptyHostFactory.Start(executablePath, arguments, resolvedWorkingDirectory, environment, columns, rows);
    }

    /// <summary>
    /// Fans the shared MCP registry (#26) into the interactive TUI by writing a registry-only
    /// <c>--mcp-config</c> file (no cockpit permission server — the TUI prompts for permission itself) and
    /// returning its path, or <see langword="null"/> when the registry has no Claude-eligible server. Sync
    /// (matching <see cref="Launch"/>'s synchronous spawn path) and best-effort: any failure just launches the
    /// session without the shared servers rather than blocking it.
    /// </summary>
    private string? _WriteRegistryMcpConfig()
    {
        try
        {
            var registry = _mcpServerStore.LoadAsync().GetAwaiter().GetResult();
            var json = McpConfigFile.SerializeRegistryOnly(registry);
            if (json is null)
            {
                return null;
            }

            var path = Path.Combine(Path.GetTempPath(), $"cockpit-tty-mcp-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the launch-only start-default flags for the TTY spawn. Extracted (and <c>internal</c>)
    /// so the flag construction is unit-testable without a real pty. Deliberately narrower than
    /// <c>ClaudeCliProcess.BuildArguments</c> — no <c>-p</c>/stream-json/permission-prompt-tool wiring,
    /// since TTY mode runs the genuine interactive TUI (it prompts for permission itself). The session id
    /// is deliberately <em>not</em> forced (<c>--session-id</c> is undocumented for a new interactive
    /// session and does not persist a transcript under that id) — the cockpit instead locates the live
    /// transcript as the new file that appears after launch (see <c>ISessionTranscriptReader</c>).
    /// </summary>
    internal static List<string> BuildArguments(string? permissionMode, string? model, string? effort, string? mcpConfigPath = null)
    {
        var arguments = new List<string>();

        // Bypass is a launch-only synonym for --dangerously-skip-permissions; the CLI does not accept
        // both flags together, so the two are mutually exclusive here (mirrors ClaudeCliProcess's
        // bypass handling, which likewise skips --permission-mode in that case).
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
        // TTY session gains the registry's servers without losing the user's own .mcp.json (strict would
        // replace them). No permission server here — the TUI handles permission prompts itself.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfigPath);
        }

        return arguments;
    }

    /// <summary>
    /// Snapshots the cockpit process's own environment as the base the pty child inherits from —
    /// a ConPTY child gets no environment unless we hand it one (HOME/USERPROFILE, PATH, APPDATA, ...);
    /// Porta.Pty inherits automatically but we still want the base explicit here so both platforms
    /// go through the identical <see cref="TtyEnvironment.Build"/> composition.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CurrentProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }
}
