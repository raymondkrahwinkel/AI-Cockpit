using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Default <see cref="ITtyLauncher"/>: builds the host environment, asks the provider how its CLI starts, and
/// spawns it in a pseudo console. Platform-agnostic — the pty host itself (ConPTY on Windows, Porta.Pty on
/// Linux/macOS) is <see cref="IPtyHostFactory"/>.
/// </summary>
/// <remarks>
/// Nothing here knows which agent is running. That is the point of the split: the pieces that were provider-
/// specific (executable, flags, config directory, status relay) moved into <see cref="ITtySessionProvider"/>,
/// and what is left is the part every TUI needs identically.
/// </remarks>
internal sealed class TtyLauncher(IPtyHostFactory ptyHostFactory, ILogger<TtyLauncher> logger) : ITtyLauncher, ISingletonService
{
    public IConPtyProcess Launch(
        ITtySessionProvider provider,
        SessionProfile? profile,
        IReadOnlyDictionary<string, string> options,
        short columns,
        short rows,
        string? workingDirectory = null,
        SessionResume? resume = null)
    {
        var baseEnvironment = TtyEnvironment.BuildBase(CurrentProcessEnvironment());

        // The profile's own variables (AC-22) sit between the inherited base and the provider's overlay: they
        // override what the cockpit inherited, and the provider keeps the last word — its overlay carries
        // functional isolation (a config directory), which an operator variable must not be able to break.
        if (profile?.EnvironmentVariables is { Count: > 0 } profileVariables)
        {
            var profileOverlay = ProfileEnvironmentVariable.ToOverlay(profileVariables);
            if (TtyEnvironment.RejectedOverlayKeys(profileOverlay) is { Count: > 0 } rejectedProfileKeys)
            {
                logger.LogWarning(
                    "Profile {Profile} configures host-controlled environment variables; ignored: {Variables}",
                    profile.Label,
                    string.Join(", ", rejectedProfileKeys));
            }

            baseEnvironment = TtyEnvironment.Compose(baseEnvironment, profileOverlay);
        }

        var context = new TtyLaunchContext(
            profile,
            options,
            Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory()),
            resume,
            baseEnvironment);

        var spec = provider.BuildLaunch(context);

        // A provider that tries to set what the host strips gets ignored, not obeyed — but never silently: this
        // is either a bug in the provider or an attempt to hand the child a credential the operator never chose.
        // The names are safe to log; the values are the secret, and those are exactly what we drop.
        if (TtyEnvironment.RejectedOverlayKeys(spec.EnvironmentOverlay) is { Count: > 0 } rejected)
        {
            logger.LogWarning(
                "TTY provider {ProviderId} tried to set host-controlled environment variables; ignored: {Variables}",
                provider.ProviderId,
                string.Join(", ", rejected));
        }

        var environment = TtyEnvironment.Compose(baseEnvironment, spec.EnvironmentOverlay);
        var process = ptyHostFactory.Start(spec.ExecutablePath, spec.Arguments, spec.WorkingDirectory, environment, columns, rows);

        // The files the launch wrote live exactly as long as the session that needs them: an MCP config holds the
        // registry's bearer headers, and the limits of a session that has ended are nobody's business.
        return spec.SessionScopedFiles.Count is 0 && spec.StatusFile is null
            ? process
            : new TtyProcessOwningSessionFiles(process, spec.SessionScopedFiles, spec.StatusFile);
    }

    /// <summary>
    /// Snapshots the cockpit process's own environment as the base the pty child inherits from — a ConPTY child
    /// gets no environment unless we hand it one (HOME/USERPROFILE, PATH, APPDATA, ...); Porta.Pty inherits
    /// automatically but the base stays explicit here so both platforms compose identically.
    /// </summary>
    private static Dictionary<string, string> CurrentProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }
}
