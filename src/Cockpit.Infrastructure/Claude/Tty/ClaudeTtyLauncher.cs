using System.Collections;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude;
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

    public ClaudeTtyLauncher(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter,
        IPtyHostFactory ptyHostFactory)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
        _ptyHostFactory = ptyHostFactory;
    }

    public IConPtyProcess Launch(
        ClaudeProfile? profile,
        string? permissionMode,
        string? model,
        string? effort,
        short columns,
        short rows)
    {
        var cli = _options.Claude;
        var workingDirectory = string.IsNullOrWhiteSpace(cli.WorkingDirectory)
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
                Path.GetFullPath(workingDirectory));
        }

        var executablePath = profile?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        var environment = TtyEnvironment.Build(CurrentProcessEnvironment(), profile, userHome);
        var arguments = BuildArguments(permissionMode, model, effort);

        return _ptyHostFactory.Start(executablePath, arguments, workingDirectory, environment, columns, rows);
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
    internal static List<string> BuildArguments(string? permissionMode, string? model, string? effort)
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
