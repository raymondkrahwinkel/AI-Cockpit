using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

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
internal sealed class TtyLauncher(IPtyHostFactory ptyHostFactory, McpAuthKey authKey, SessionMcpKeyring keyring, ILogger<TtyLauncher> logger) : ITtyLauncher, ISingletonService
{
    public IConPtyProcess Launch(
        ITtySessionProvider provider,
        SessionProfile? profile,
        IReadOnlyDictionary<string, string> options,
        short columns,
        short rows,
        string? workingDirectory = null,
        SessionResume? resume = null,
        string? paneId = null,
        IReadOnlySet<string>? enabledMcpServerNames = null)
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

        // AC-13: hand the session its own pane id so the agent can name itself to the cockpit-session MCP's
        // set_status tool. Set after the profile's variables (a host-owned identity a profile must not shadow) and
        // before the provider's overlay, which still keeps the last word.
        if (!string.IsNullOrEmpty(paneId))
        {
            baseEnvironment = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["COCKPIT_PANE_ID"] = paneId,
            };
        }

        // AC-40: this run's MCP auth key, so a cockpit-hosted server's --mcp-config can reference COCKPIT_MCP_KEY
        // (Claude's Bearer ${COCKPIT_MCP_KEY}, Codex's bearer_token_env_var) instead of embedding a literal, and the
        // child presents it to the 401 gate. It has to go on the base, not a provider overlay: an overlay value is
        // scrubbed as host-controlled (a profile/provider must not override the key and lock the session out with a
        // self-inflicted 401), and a base value the host sets itself is what survives Compose down to the child. The
        // host owns it here for the same reason it owns the pane id above — set after the profile's variables, which
        // Compose has already laid down, so no profile can shadow it. Without this the env reference expands to empty
        // and every cockpit-hosted MCP endpoint answers 401 (unlike the in-process local-model loop and the SDK
        // spawn, which hand the key straight to the client and so were never affected).
        // AC-89: when this session has a pane id, hand it its own per-session token instead of the shared app key, so
        // a request from it can be attributed to this pane and the consent broker cannot be tricked by another pane's
        // agent claiming this session's id. Without a pane id (no session to name) it falls back to the shared key.
        baseEnvironment = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            [WellKnownSessionEnvironment.CockpitMcpKey] = string.IsNullOrEmpty(paneId) ? authKey.Value : keyring.TokenFor(paneId),
        };

        var context = new TtyLaunchContext(
            profile,
            options,
            Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory()),
            resume,
            baseEnvironment)
        {
            // The per-session MCP checklist (#44): a provider that fans the shared registry into its config narrows
            // to exactly these names, so an unchecked server never reaches the CLI. Null means no narrowing.
            EnabledMcpServerNames = enabledMcpServerNames,
        };

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
