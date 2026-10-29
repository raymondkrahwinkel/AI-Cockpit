using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Consent;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Sessions;

// AC-1911/AC-1908: elevation is an explicitly selected capability, separate from the always-mounted session
// plumbing. Consent and UAC remain independent gates on every invocation.
internal sealed class ElevatedSessionTools(
    IConsentBroker? consent = null,
    IElevatedCommandRunner? elevated = null,
    IOrgPolicy? orgPolicy = null)
{
    private const int MaxTimeoutSeconds = 900;
    private const int DefaultTimeoutSeconds = 300;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "run_elevated", ReadOnly = false, Destructive = true)]
    [Description("Runs ONE PowerShell command as administrator on the operator's Windows machine — the route for anything that needs elevation (`Get-WindowsOptionalFeature`, `wsl --install`, a service or firewall change): use this rather than asking the operator to open an administrator terminal or trying `runas` yourself. Two gates on every call: the cockpit first shows the operator a consent card with the exact command line, the working directory and your `reason`; only after approval does Windows show its own UAC prompt. Nothing is remembered — each call is one card and one prompt — so put what belongs together into one command and never call this in a loop. The command runs in a hidden window with `-NoProfile -NonInteractive`; anything that would wait for a keyboard errors out instead. Its whole output (stdout, errors, everything) comes back in `output` once it ends. Windows only.")]
    public async Task<string> RunElevatedAsync(
        [Description("The PowerShell command to run as administrator, e.g. 'Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux'. Shown to the operator verbatim; it is run as one script block.")] string command,
        [Description("The directory the command runs in.")] string workingDirectory,
        [Description("Why this needs administrator rights, in one sentence. Shown to the operator on the consent card as your own words.")] string reason,
        [Description("How long the command may run before it is ended, in seconds. Defaults to 300 and cannot be set higher than 900.")] int timeoutSeconds = DefaultTimeoutSeconds,
        [Description("Optional. Your session id — the value of the COCKPIT_PANE_ID environment variable in this session. Only needed as a fallback when automatic session identification is unavailable.")] string? session = null)
    {
        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (string.IsNullOrEmpty(caller))
        {
            return _Error("Could not identify your session and no `session` was given — pass the COCKPIT_PANE_ID environment variable as `session`.");
        }

        if (OrgPolicyCeilings.ShellForbidden(orgPolicy?.Current))
        {
            return _Error($"{orgPolicy?.Current?.By} does not allow agents to run commands on this machine.");
        }

        if (elevated is null || !elevated.IsSupported)
        {
            return _Error("Running a command as administrator is a Windows-only action.");
        }

        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(reason))
        {
            return _Error("`command`, `workingDirectory` and `reason` are all required.");
        }

        if (consent is null)
        {
            return _Error("No consent surface is available, so nothing was run as administrator.");
        }

        var plan = elevated.Plan(command);
        var decision = await consent.RequestConsentAsync(new ConsentRequest(
            "A session wants to run a command as administrator",
            $"{plan.CommandLine}\nin {workingDirectory}\nReason given by the agent (not verified): {reason}",
            new ConsentSource(caller, null, ConsentSourceCatalog.ElevatedCommand),
            "session.elevated-command",
            ConsentRisk.Dangerous,
            AllowRemember: false)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return JsonSerializer.Serialize(
                new { ok = false, declined = "consent", error = "The operator did not approve running this command as administrator; nothing ran." },
                SerializerOptions);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds));
        var result = await elevated.RunAsync(plan, workingDirectory, timeout).ConfigureAwait(false);
        return result.Outcome switch
        {
            ElevatedCommandOutcome.Declined => JsonSerializer.Serialize(
                new { ok = false, declined = "uac", error = "The operator declined the Windows UAC prompt; nothing ran." }, SerializerOptions),
            ElevatedCommandOutcome.Failed => _Error(result.Error ?? "The elevated command could not be started."),
            ElevatedCommandOutcome.TimedOut => JsonSerializer.Serialize(
                new { ok = false, timedOut = true, exitCode = result.ExitCode, output = result.Output, error = result.Error }, SerializerOptions),
            _ => JsonSerializer.Serialize(new { ok = true, exitCode = result.ExitCode, output = result.Output, error = result.Error }, SerializerOptions),
        };
    }

    private static string _Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message }, SerializerOptions);
}
