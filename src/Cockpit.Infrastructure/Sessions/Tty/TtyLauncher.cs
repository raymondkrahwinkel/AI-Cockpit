using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// Default `ITtyLauncher`: builds the host environment, asks the provider how its CLI starts, and spawns it in a
// pseudo console (`IPtyHostFactory`, platform-agnostic). Provider-specific pieces (executable, flags, config
// directory, status relay) live in `ITtySessionProvider`; what's left here is what every TUI needs identically.
internal sealed class TtyLauncher(IPtyHostFactory ptyHostFactory, ISessionMemoryLimiter memoryLimiter, McpAuthKey authKey, SessionMcpKeyring keyring, ILogger<TtyLauncher> logger) : ITtyLauncher, ISingletonService
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
        IReadOnlySet<string>? enabledMcpServerNames = null,
        SessionResources? contributed = null,
        string? projectId = null)
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

        // AC-165: what the plugins give this session, on top of the profile's own variables so a project's
        // answer beats the profile's default — same precedence as the SDK route. Still ahead of the host
        // identity and the provider's overlay below, which keep the last word.
        if (contributed is { IsEmpty: false })
        {
            var contributedOverlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in contributed.EnvironmentVariables)
            {
                contributedOverlay[key] = value;
            }

            baseEnvironment = TtyEnvironment.Compose(baseEnvironment, contributedOverlay);
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

        // AC-1013: AC-40's MCP auth key must sit on the base environment, not a provider overlay (an overlay value is
        // scrubbed as host-controlled), so no profile/provider can override it and self-lock the session out with a
        // 401. AC-89: a pane id gets its own per-session token instead of the shared key, for attribution; AC-143 keeps it so this route's own teardown (TtyProcessOwningSessionFiles) can revoke it.
        var mintedToken = string.IsNullOrEmpty(paneId) ? null : keyring.TokenFor(paneId);
        baseEnvironment = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            [WellKnownSessionEnvironment.CockpitMcpKey] = mintedToken ?? authKey.Value,
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
            // AC-218: the project this session runs under, so the fan-out below resolves against that project's
            // own registry view (its servers, its by-name overrides) rather than the unscoped registry.
            ProjectId = projectId,
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

        // AC-661: the OS ceiling around this session's tree, applied before the CLI has spawned anything of its own,
        // so everything it starts later is born inside it.
        var memoryCap = memoryLimiter.Apply(process.ProcessId, SessionMemoryCap.ResolveBytes(profile, options));

        // The files this launch wrote live exactly as long as the session needing them (an MCP config holds bearer
        // headers, no business surviving the session that ends). AC-143: a minted pane token needs the same
        // wrapping so its revoke runs on dispose, even when the provider itself wrote no session-scoped files.
        return spec.SessionScopedFiles.Count is 0 && spec.StatusFile is null && mintedToken is null && memoryCap is null
            ? process
            : new TtyProcessOwningSessionFiles(process, spec.SessionScopedFiles, spec.StatusFile, mintedToken is null ? null : keyring, paneId, mintedToken, memoryCap);
    }

    // Snapshots the cockpit process's own environment as the base the pty child inherits from — a ConPTY child
    // gets no environment unless we hand it one (HOME/USERPROFILE, PATH, APPDATA, ...); Porta.Pty inherits
    // automatically but the base stays explicit here so both platforms compose identically.
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
