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

        return _ptyHostFactory.Start(executablePath, workingDirectory, environment, columns, rows);
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
