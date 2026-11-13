using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Vault;

// AC-2346: the `cockpit-vault` MCP server. A value is injected into a process the hub starts itself and never
// returned: the only way back to the agent is the output, and the hub redacts that first.
internal sealed class VaultMcpTools
{
    internal const string EncryptionOffText =
        "Vault unavailable: Cockpit's credential encryption is off. Ask the user to turn it on in Options › Security › Encryption (\"Encrypt with a password…\").";

    internal const string LockedText = "Vault unavailable: Cockpit's credential encryption is locked. Ask the user to unlock Cockpit.";

    internal const int OutputLimit = 64 * 1024;

    private const int MaxTimeoutSeconds = 600;
    private const string Once = "once";
    private const string Session = "session";
    private const string Project = "project";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly VaultHub _hub;
    private readonly IConsentBroker _consent;
    private readonly ISessionRegistry _sessions;
    private readonly ILogger<VaultMcpTools> _logger;
    private readonly IProjectStore? _projects;

    public VaultMcpTools(VaultHub hub, IConsentBroker consent, ISessionRegistry sessions, ILogger<VaultMcpTools> logger, IProjectStore? projects = null)
    {
        _hub = hub;
        _consent = consent;
        _sessions = sessions;
        _logger = logger;
        _projects = projects;
        sessions.Changed += (_, _) => hub.EndSessionsExcept(_LivePaneIds());
    }

    [McpServerTool(Name = "vault_list", ReadOnly = true)]
    [Description("Lists the secrets the vault offers: per item its reference, name, provider and the grant your session holds for it (none, session or project). Never returns a value; use vault_run to run a command with one in its environment.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        if (await _UnavailableAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }

        var session = _CallerSession();
        var (sessionGrants, projectGrants) = session is null
            ? (new HashSet<string>(), new HashSet<string>())
            : await _hub.GrantsForAsync(session.PaneId, session.ProjectId, cancellationToken).ConfigureAwait(false);
        var items = new List<object>();
        var errors = new List<string>();
        foreach (var provider in _hub.Providers)
        {
            if (await _ItemsAsync(provider, cancellationToken).ConfigureAwait(false) is not { } listed)
            {
                errors.Add(_ProviderError(provider));
                continue;
            }

            items.AddRange(listed.Select(item => new
            {
                reference = item.Reference,
                name = item.DisplayName,
                provider = provider.DisplayName,
                grant = projectGrants.Any(key => VaultHub.ReferenceOf(key) == item.Reference) ? Project
                    : sessionGrants.Any(key => VaultHub.ReferenceOf(key) == item.Reference) ? Session : "none",
            }));
        }

        return _Serialize(new { ok = true, items, errors });
    }

    [McpServerTool(Name = "vault_run", ReadOnly = false, Destructive = true)]
    [Description("Runs one command with vault secrets in its environment, after the operator approves it on a card. The command runs without a shell, in your session's working directory; each env entry maps an environment variable to a vault reference from vault_list. Returns exitCode, output (stdout then stderr, the last 64 KB) and masked: every literal occurrence of an injected value in the output is replaced by [vault: <name>]. The value itself is never returned. A grant covers exactly this variable, reference and argv; any other command asks again.")]
    public async Task<string> RunAsync(
        [Description("The executable and its arguments, one per entry — never re-parsed by a shell.")] string[] argv,
        [Description("Environment variables to set, each mapped to a vault reference, e.g. {\"API_TOKEN\": \"op://Agents/deploy/token\"}.")] Dictionary<string, string> env,
        [Description("Why this command needs the secret, in one sentence; the operator sees it on the card as your words.")] string reason,
        [Description("How long to wait for the operator and then for the command, in seconds. Default and maximum 600.")] int timeoutSeconds = MaxTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (await _UnavailableAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }

        if (_CallerSession() is not { WorkingDirectory: { Length: > 0 } directory } session)
        {
            return _Error("vault_run needs a session of this Cockpit with a working directory.");
        }

        if (argv is not [{ Length: > 0 }, ..] || env is not { Count: > 0 } || env.Keys.Any(name => name.Length == 0 || name.Contains('=')))
        {
            return _Error("argv needs an executable and env at least one valid variable name.");
        }

        var injections = new List<(string Variable, string Reference, string Name, IVaultProvider Provider)>();
        foreach (var (variable, reference) in env)
        {
            if (_hub.ProviderFor(reference) is not { } provider)
            {
                return _Error($"No vault provider handles '{reference}'.");
            }

            if (await _ItemsAsync(provider, cancellationToken).ConfigureAwait(false) is not { } items)
            {
                return _Error(_ProviderError(provider));
            }

            if (items.FirstOrDefault(item => item.Reference == reference) is not { } item)
            {
                return _Error($"'{reference}' is not an item of {provider.DisplayName}; see vault_list.");
            }

            injections.Add((variable, reference, item.DisplayName, provider));
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds));
        foreach (var injection in injections)
        {
            await _hub.AuditAsync("Requested", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!await _ConsentAsync(session, directory, argv, injections.Select(i => (i.Variable, i.Reference, i.Name)).ToList(), reason, timeout, cancellationToken).ConfigureAwait(false))
        {
            foreach (var injection in injections)
            {
                await _hub.AuditAsync("Denied", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return _Serialize(new { ok = false, denied = true, error = "Denied: the operator did not allow this command; nothing was started." });
        }

        var values = new List<(string Variable, string Value, string Name)>();
        foreach (var injection in injections)
        {
            try
            {
                values.Add((injection.Variable, await injection.Provider.ResolveAsync(injection.Reference, cancellationToken).ConfigureAwait(false), injection.Name));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning("Vault provider '{ProviderId}' failed to resolve an item ({ExceptionType}).", injection.Provider.Id, exception.GetType().Name);
                return _Error(_ProviderError(injection.Provider));
            }
        }

        var run = await _StartAsync(argv, directory, values, timeout, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return _Error($"Could not start '{argv[0]}'.");
        }

        foreach (var injection in injections)
        {
            await _hub.AuditAsync("Injected", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var (output, masked) = Redact(run.Value.Output, values.Select(v => (v.Value, v.Name)));
        await _hub.AuditAsync("Masked", session.PaneId, string.Join(", ", injections.Select(i => i.Name)), masked, cancellationToken).ConfigureAwait(false);
        return _Serialize(new
        {
            ok = true,
            exitCode = run.Value.ExitCode,
            timedOut = run.Value.TimedOut,
            output = output.Length > OutputLimit ? output[^OutputLimit..] : output,
            masked,
        });
    }

    // Longest value first, so a value that contains another is replaced whole.
    internal static (string Output, int Masked) Redact(string output, IEnumerable<(string Value, string Name)> values)
    {
        var masked = 0;
        foreach (var (value, name) in values.Where(v => v.Value.Length > 0).OrderByDescending(v => v.Value.Length))
        {
            var parts = output.Split(value);
            masked += parts.Length - 1;
            output = string.Join($"[vault: {name}]", parts);
        }

        return (output, masked);
    }

    private async Task<bool> _ConsentAsync(
        ISessionHandle session, string directory, string[] argv, List<(string Variable, string Reference, string Name)> pairs, string reason,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (sessionGrants, projectGrants) = await _hub.GrantsForAsync(session.PaneId, session.ProjectId, cancellationToken).ConfigureAwait(false);
        var keys = pairs.Select(pair => VaultHub.GrantKey(pair.Reference, pair.Variable, argv)).ToList();
        if (keys.All(key => sessionGrants.Contains(key) || projectGrants.Contains(key)))
        {
            return true;
        }

        var projectName = session.ProjectId is { } projectId && _projects is not null
            ? (await _projects.LoadAsync(cancellationToken).ConfigureAwait(false)).Find(projectId)?.Name ?? projectId
            : session.ProjectId;
        ConsentDecision decision;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            decision = await _consent.RequestConsentAsync(Card(session.Title, session.PaneId, directory, argv, pairs.Select(pair => (pair.Variable, pair.Reference)), reason, projectName), wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        var scope = decision is { IsApproved: true, Chosen: { } chosen } && chosen.TryGetValue("scope", out var picked) && picked.Count == 1 ? picked[0] : null;
        switch (scope)
        {
            case Session:
                _hub.GrantSession(session.PaneId, keys);
                break;
            case Project when session.ProjectId is { } project:
                if (!await _hub.TryGrantProjectAsync(project, keys, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                break;
            case Once:
                break;
            default:
                return false;
        }

        foreach (var pair in pairs)
        {
            await _hub.AuditAsync("Allowed", session.PaneId, pair.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    internal static ConsentRequest Card(
        string sessionTitle, string paneId, string directory, IReadOnlyList<string> argv, IEnumerable<(string Variable, string Reference)> pairs, string reason, string? projectName)
    {
        var command = string.Join(' ', argv);
        var said = string.Join(' ', (reason ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new ConsentRequest(
            $"{sessionTitle} wants to run a command with a vault secret",
            string.Join('\n', pairs.Select(pair => $"{pair.Variable} ← {pair.Reference}"))
            + $"\nin {JsonSerializer.Serialize(argv, SerializerOptions)}\nin {directory}"
            + (said.Length > 0 ? $"\nThe agent says: \"{said}\"" : string.Empty),
            new ConsentSource(paneId, null, ConsentSourceCatalog.VaultMcp),
            "vault.run",
            ConsentRisk.Dangerous)
        {
            Choices =
            [
                new ConsentChoice("scope", "Allow", [
                    new ConsentOption(Once, "Once", $"For this one start of {command}. The next start asks again.", Selected: true),
                    new ConsentOption(Session, "For this session", $"Every start of {command} in this session, until it ends or you revoke it."),
                    new ConsentOption(Project, projectName is null ? "Always for this project" : $"Always for project {projectName}",
                        projectName is null ? null : $"Every session in {projectName}, now and later, without asking. Revoke it under Options › Vault.", Enabled: projectName is not null)
                    {
                        Note = projectName is null ? "no project" : null,
                    },
                ]),
            ],
            ApproveLabel = "Run",
            Never = ["The value in the agent's output", "Any other command or variable"],
        };
    }

    // ponytail: reads the whole output before trimming to the last 64 KB; stream it if a command prints gigabytes.
    private static async Task<(int ExitCode, string Output, bool TimedOut)?> _StartAsync(
        string[] argv, string directory, List<(string Variable, string Value, string Name)> values, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(CommandRunnerProcess.NormalizeExecutablePath(argv[0]))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in argv.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (variable, value, _) in values)
        {
            startInfo.Environment[variable] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        var readOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var readError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            CommandRunnerProcess._KillTree(process);
        }

        var output = await CommandRunnerProcess._DrainAsync(readOutput).ConfigureAwait(false)
            + await CommandRunnerProcess._DrainAsync(readError).ConfigureAwait(false);
        return (timedOut || !process.HasExited ? -1 : process.ExitCode, output, timedOut);
    }

    private async Task<string?> _UnavailableAsync(CancellationToken cancellationToken)
    {
        var availability = await _hub.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        if (availability == VaultAvailability.Available)
        {
            return null;
        }

        _hub.ShowUnavailable();
        return _Error(availability == VaultAvailability.Locked ? LockedText : EncryptionOffText);
    }

    private async Task<IReadOnlyList<VaultItem>?> _ItemsAsync(IVaultProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ListItemsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Vault provider '{ProviderId}' failed to list its items ({ExceptionType}).", provider.Id, exception.GetType().Name);
            return null;
        }
    }

    // The caller is the pane the transport verified, never an argument.
    private ISessionHandle? _CallerSession() => McpRequestContext.CurrentPaneId is not { } paneId
        ? null
        : _sessions.Find(paneId) ?? (_sessions.Assistant is { } assistant && assistant.PaneId == paneId ? assistant : null);

    private HashSet<string> _LivePaneIds()
    {
        var live = _sessions.All.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        if (_sessions.Assistant is { } assistant)
        {
            live.Add(assistant.PaneId);
        }

        return live;
    }

    private static string _ProviderError(IVaultProvider provider) =>
        $"{provider.DisplayName} is not available: it is not connected or it failed. Ask the user to check its connection in Cockpit.";

    private static string _Error(string message) => _Serialize(new { ok = false, error = message });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
