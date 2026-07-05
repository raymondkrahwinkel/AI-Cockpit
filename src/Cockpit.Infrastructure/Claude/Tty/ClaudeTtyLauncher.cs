using System.Collections;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude.Tty;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude.Tty;

/// <summary>
/// Default <see cref="IClaudeTtyLauncher"/>: spawns the interactive <c>claude</c> TUI inside a
/// ConPTY, reusing the SDK-mode profile/executable/trust plumbing.
/// </summary>
internal sealed class ClaudeTtyLauncher : IClaudeTtyLauncher, ISingletonService
{
    private readonly CockpitOptions _options;
    private readonly IClaudeExecutableLocator _executableLocator;
    private readonly WorkspaceTrustWriter _workspaceTrustWriter;

    public ClaudeTtyLauncher(
        IOptions<CockpitOptions> options,
        IClaudeExecutableLocator executableLocator,
        WorkspaceTrustWriter workspaceTrustWriter)
    {
        _options = options.Value;
        _executableLocator = executableLocator;
        _workspaceTrustWriter = workspaceTrustWriter;
    }

    public IConPtyProcess Launch(ClaudeProfile? profile, short columns, short rows)
    {
        var cli = _options.Claude;
        var workingDirectory = string.IsNullOrWhiteSpace(cli.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : cli.WorkingDirectory;

        if (profile is not null)
        {
            // Same rule as SDK mode: trust must land before the process starts, or the TUI blocks
            // on its interactive trust dialog on first render.
            _workspaceTrustWriter.MarkWorkingDirectoryTrusted(profile.ConfigDir, Path.GetFullPath(workingDirectory));
        }

        var executablePath = profile?.ExecutablePath
            ?? _executableLocator.FindBundledExecutable()
            ?? cli.ExecutablePath;

        var environment = TtyEnvironment.Build(CurrentProcessEnvironment(), profile);

        return ConPtyProcess.Start(QuoteExecutable(executablePath), workingDirectory, environment, columns, rows);
    }

    /// <summary>
    /// Snapshots the cockpit process's own environment as the base the pty child inherits from —
    /// a ConPTY child gets no environment unless we hand it one (HOME/USERPROFILE, PATH, APPDATA, ...).
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

    /// <summary>
    /// Wraps the executable path in quotes when it contains spaces (the bundled path lives under
    /// <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c>). Passed as the whole command
    /// line to <c>CreateProcessW</c>, which parses argv itself.
    /// </summary>
    internal static string QuoteExecutable(string executablePath) =>
        executablePath.Contains(' ') && !executablePath.StartsWith('"')
            ? $"\"{executablePath}\""
            : executablePath;
}
