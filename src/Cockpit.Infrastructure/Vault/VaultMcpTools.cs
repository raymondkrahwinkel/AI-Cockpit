using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    [Description("Lists the secrets the vault offers: per item its reference, name, provider and the grant your session holds for it (none, session or project). Never returns a value; use vault_run with env or stdinReference. After vault_write creates an item, list again to obtain its reference.")]
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
                providerId = provider.Id,
                grant = projectGrants.Any(key => VaultHub.ReferenceOf(key) == item.Reference) ? Project
                    : sessionGrants.Any(key => VaultHub.ReferenceOf(key) == item.Reference) ? Session : "none",
            }));
        }

        return _Serialize(new { ok = true, items, errors });
    }

    [McpServerTool(Name = "vault_run", ReadOnly = false, Destructive = true)]
    [Description("Runs one command without a shell with env mappings, one stdinReference and/or files, after operator approval. Stdin receives the value plus one newline, then EOF; SSH forwards stdin, not env. Output (bounded at 65,536 characters per stream; overflow terminates the command and suppresses output) masks literal injected values and common base64/base64url/URL forms as [vault: <name>]. Structured grants separately bind reference, channel, verification mode, variable name, exact argv, stdin framing and updateOnSuccess target; verification grants never permit output-returning runs. Recipes using scripts/vault-recipes.py: (1) vault_write generate, vault_list, then ssh host python3 helper db mariadb/postgres create USER via stdin; (2) generate a separate staged replacement, run db rotate via stdin with updateOnSuccess=existing reference, so failed apply never overwrites the vault; (3) import one remote env field via vault_write command, not vault_run; (4) helper config PATH --key KEY over ssh writes owner-only config atomically; (5) helper http URL formats the stdin token for curl -K -, never argv; (6) helper container --pane PANE IMAGE COMMAND supplies a Linux Docker env-file through /dev/stdin, without a temporary secret file. A local wrapper can combine env credentials and stdin before SSH. Multi-field references can populate several env variables; compose connection strings inside the child. files maps environment variable names to private UTF-8 file paths for SSH keys, PEM and JSON documents; file permissions and lifetime are host-managed on Windows/Linux. Use stdin for gh secret set NAME, kubectl --from-file=key=/dev/stdin and docker secret create NAME -; use files for .npmrc via NPM_CONFIG_USERCONFIG. verifyOnly returns only success/failure and has a distinct grant: use it for login/ping and composite-value probes. Masking is not a general DLP boundary: arbitrary splitting, escaping or derived/composite encodings require verifyOnly and a consumer that does not log credentials. Consumers must fail atomically; on uncertain remote completion, recover with the retained staged item rather than generate again.")]
    public async Task<string> RunAsync(
        [Description("The executable and its arguments, one per entry — never re-parsed by a shell.")] string[] argv,
        [Description("ASCII environment variable names ([A-Za-z_][A-Za-z0-9_]*) mapped to vault references, e.g. {\"API_TOKEN\": \"op://Agents/deploy/token\"}.")] Dictionary<string, string> env,
        [Description("Why this command needs the secret, in one sentence; the operator sees it on the card as your words.")] string reason,
        [Description("How long to wait for the operator and then for the command, in seconds. Default and maximum 600.")] int timeoutSeconds = MaxTimeoutSeconds,
        [Description("One vault reference to send privately to stdin as value plus newline, then EOF. Use env={} for stdin alone; env may supply a second secret to a local wrapper.")] string? stdinReference = null,
        [Description("Existing vault field to update with stdin only after exit 0 and successful delivery. Stage a separate replacement first. A shared native-item lock covers remote apply and commit, including direct writes; changed revisions refuse the commit and retain staging. External clients are outside that lock. Requires provider writes and appears on the same approval card.")] string? updateOnSuccess = null,
        [Description("ASCII variable names mapped to vault references. Each variable contains a private temporary file path with exact UTF-8 bytes, no added newline. Windows uses per-user Cockpit runtime state with verified current-user-only DACLs and requires delete-sharing readers; Linux uses anonymous memfd. Files disappear on completion, timeout, child crash or host death; permission failures refuse execution.")] Dictionary<string, string>? files = null,
        [Description("Return only {ok:true/false} for login/ping probes; discard all command output, including encoded/composite values. Uses its own approval scope and cannot be combined with updateOnSuccess.")] bool verifyOnly = false,
        [Description("Append a newline to stdin (default true, convenient for read -r over SSH). Set false for exact UTF-8 bytes when a platform secret command must store the value without an extra newline.")] bool stdinNewline = true,
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

        if (argv is not [{ Length: > 0 }, ..] || env is null || (env.Count == 0 && stdinReference is null && files is not { Count: > 0 })
            || env.Keys.Concat(files?.Keys.AsEnumerable() ?? []).Any(name => name.Length == 0 || (!char.IsAsciiLetter(name[0]) && name[0] != '_')
                || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            || (files is not null && files.Keys.Any(env.ContainsKey))
            || (updateOnSuccess is not null && (stdinReference is null || verifyOnly)))
        {
            return _Error("Provide argv and at least one env, stdinReference or files mapping with distinct variable names; updateOnSuccess requires stdinReference and cannot verifyOnly.");
        }

        if (files is { Count: > 0 } && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return _Error("The private file channel requires Windows or Linux; use stdin on this platform.");
        }

        var injections = new List<(VaultChannel Channel, string Variable, string Reference, string Name, IVaultProvider Provider)>();
        IEnumerable<(VaultChannel Channel, string Variable, string Reference)> channels = env.Select(pair => (VaultChannel.Env, pair.Key, pair.Value));
        if (stdinReference is not null)
        {
            channels = channels.Append((VaultChannel.Stdin, string.Empty, stdinReference));
        }

        channels = channels.Concat(files?.Select(pair => (VaultChannel.File, pair.Key, pair.Value)) ?? []);
        foreach (var (channel, variable, reference) in channels)
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
                await _hub.AuditAsync("Denied", session.PaneId, reference, cancellationToken: cancellationToken).ConfigureAwait(false);
                return _Error($"'{reference}' is not an item of {provider.DisplayName}; see vault_list.");
            }

            injections.Add((channel, variable, reference, item.DisplayName, provider));
        }

        IVaultProvider? updateProvider = null;
        VaultItem? updateItem = null;
        VaultItemVersion? expectedVersion = null;
        var grants = injections.Select(i => new VaultGrant(i.Channel, verifyOnly, i.Variable, i.Reference, argv, updateOnSuccess, stdinNewline)).ToList();
        var consentPairs = injections.Select(i => (Variable: $"{i.Channel}: {i.Variable}" + (verifyOnly ? " (verify only)" : string.Empty)
            + (i.Channel == VaultChannel.Stdin && !stdinNewline ? " (exact bytes)" : string.Empty), i.Reference, i.Name)).ToList();
        if (updateOnSuccess is not null)
        {
            updateProvider = _hub.ProviderFor(updateOnSuccess);
            if (updateProvider is not { CanWrite: true }
                || await _ItemsAsync(updateProvider, cancellationToken).ConfigureAwait(false) is not { } updateItems
                || updateItems.FirstOrDefault(item => item.Reference == updateOnSuccess) is not { } target)
            {
                return _Error("The updateOnSuccess target must exist and its provider must allow agent writes; see vault_list and Options.");
            }

            updateItem = target;
            expectedVersion = await _VersionAsync(updateProvider, target.Reference, cancellationToken).ConfigureAwait(false);
            if (expectedVersion is null)
            {
                return _Error("The update target does not support a reliable revision check; nothing was started.");
            }
            grants.Add(new VaultGrant(VaultChannel.Stdin, verifyOnly, string.Empty, target.Reference, argv, target.Reference, stdinNewline));
            consentPairs.Add(($"[update after success from {stdinReference}]", target.Reference, target.DisplayName));
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds));
        foreach (var injection in injections)
        {
            await _hub.AuditAsync("Requested", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // AC-2333 S3: taken before the grants are read; a later revoke, wipe, closed gate or ended session refuses both storing
        // a grant and the start below.
        var lease = _hub.Lease();
        if (!await _ConsentAsync(lease, session, directory, argv, grants, consentPairs, reason, timeout, cancellationToken).ConfigureAwait(false))
        {
            foreach (var injection in injections)
            {
                await _hub.AuditAsync("Denied", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return _Serialize(new { ok = false, denied = true, error = "Denied: the operator did not allow this command; nothing was started." });
        }

        using IDisposable? destinationLock = updateProvider is not null && expectedVersion is not null
            ? await _hub.LockWriteAsync(updateProvider.Id, expectedVersion.ItemId, cancellationToken).ConfigureAwait(false) : null;
        if (updateProvider is not null && updateItem is not null
            && expectedVersion != await _VersionAsync(updateProvider, updateItem.Reference, cancellationToken).ConfigureAwait(false))
        {
            return _Error("The update target changed while waiting; nothing was started and the staging item was retained.");
        }

        var values = new List<(VaultChannel Channel, string Variable, string Value, string Name)>();
        foreach (var injection in injections)
        {
            try
            {
                values.Add((injection.Channel, injection.Variable, await injection.Provider.ResolveAsync(injection.Reference, cancellationToken).ConfigureAwait(false), injection.Name));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning("Vault provider '{ProviderId}' failed to resolve an item ({ExceptionType}).", injection.Provider.Id, exception.GetType().Name);
                await _hub.AuditAsync("Denied", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                return _Error(_ProviderError(injection.Provider));
            }
        }

        ContainedRun? contained = null;
        using var secretFiles = new VaultSecretFiles(_hub.RuntimeDirectory);
        bool started;
        try
        {
            var environment = new Dictionary<string, string>();
            foreach (var value in values.Where(value => value.Channel != VaultChannel.Stdin))
            {
                if (value.Channel == VaultChannel.File)
                {
                    environment[value.Variable] = secretFiles.Add(value.Value);
                }
                else
                {
                    environment[value.Variable] = value.Value;
                }
            }

            started = _hub.TryStart(
                lease,
                () => !cancellationToken.IsCancellationRequested && _CallerSession()?.PaneId == session.PaneId && (updateProvider is null || updateProvider.CanWrite),
                () => contained = CommandRunnerProcess.StartContained(argv[0], argv[1..], directory,
                    environment, stdinReference is not null));
        }
        catch (Win32Exception)
        {
            return _Error($"Could not start '{argv[0]}'.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return _Error("Could not prepare private files or start the command; no provider value is returned.");
        }

        if (!started)
        {
            foreach (var injection in injections)
            {
                await _hub.AuditAsync("Denied", session.PaneId, injection.Name, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            return _Serialize(new { ok = false, denied = true, error = "Not started: a grant was revoked, the vault closed or the session ended before the command could start." });
        }

        if (contained is null)
        {
            return _Error("The command did not start.");
        }

        string? input = stdinReference is null ? null : values.Single(value => value.Channel == VaultChannel.Stdin).Value;
        var run = await _WaitAsync(contained, timeout, cancellationToken, input: input, stdinNewline: stdinNewline).ConfigureAwait(false);

        foreach (var injection in injections)
        {
            await _hub.AuditAsync("Injected", session.PaneId, injection.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (run.OutputLimitExceeded)
        {
            return verifyOnly ? _Serialize(new { ok = false })
                : _Error("Command output exceeded 65,536 characters per stream; output was suppressed and no vault update was made. Retain any staged value.");
        }

        var (output, masked) = Redact(run.Output, values.Select(v => (v.Value, v.Name)));
        await _hub.AuditAsync("Masked", session.PaneId, string.Join(", ", injections.Select(i => i.Name)), masked, cancellationToken).ConfigureAwait(false);
        if (verifyOnly)
        {
            return _Serialize(new { ok = run.ExitCode == 0 && !run.TimedOut && run.InputSent });
        }

        bool? updated = null;
        if (updateProvider is not null && updateItem is not null && input is not null && expectedVersion is not null)
        {
            updated = false;
            if (run.ExitCode == 0 && !run.TimedOut && run.InputSent)
            {
                try
                {
                    Task? write = null;
                    if (expectedVersion == await _VersionAsync(updateProvider, updateItem.Reference, cancellationToken).ConfigureAwait(false)
                        && await _hub.AuditAsync("WriteRequested", session.PaneId, updateItem.DisplayName, cancellationToken: cancellationToken).ConfigureAwait(false)
                        && _hub.TryStart(lease, () => updateProvider.CanWrite && !cancellationToken.IsCancellationRequested && _CallerSession()?.PaneId == session.PaneId,
                            () => write = updateProvider.WriteAsync(updateItem.DisplayName, updateItem.Reference, input, expectedVersion, cancellationToken)) && write is not null)
                    {
                        await write.ConfigureAwait(false);
                        updated = true;
                        await _hub.AuditAsync("Rotated", session.PaneId, updateItem.DisplayName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning("Vault update after command failed ({ExceptionType}).", exception.GetType().Name);
                    await _hub.AuditAsync("WriteFailed", session.PaneId, updateItem.DisplayName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return _Serialize(new
        {
            ok = run.InputSent && updated is not false,
            exitCode = run.ExitCode,
            timedOut = run.TimedOut,
            output = output.Length > OutputLimit ? output[^OutputLimit..] : output,
            masked,
            updated,
            error = updated is false ? "Vault field not updated. If the remote command succeeded, recover with the staged item; do not generate another replacement." : null,
        });
    }

    [McpServerTool(Name = "vault_write", ReadOnly = false, Destructive = true)]
    [Description("Creates a concealed vault item or updates an existing field in place. Requires the provider's Allow agents to create and update items setting and a writable account. Source is command (private stdout of one command without a shell) or generate (cryptographic generation inside this tool). No literals, value output, deletion or extra consent prompt. For multi-field items predeclare nonsecret fieldNames; command stdout must be one private JSON object with exactly those string fields. Other fields remain intact. Single command-source values may be multiline PEM or raw JSON and can be delivered through vault_run files without adding a newline. Recipes: generate a new item for a remote DB user, config, HTTP credential or container, then vault_list and vault_run with env or stdinReference. For rotation generate a separate staged item; apply it through vault_run with updateOnSuccess=old reference, never overwrite the old item before application succeeds. For remote import use argv=[ssh,host,python3,/trusted/vault-recipes.py,import-field,/opt/faultline-mmo/secrets/db.env,DB_PASSWORD]; the helper emits only that field, while stderr and provider output stay private. See scripts/vault-recipes.py and vault_run for the six end-to-end recipes.")]
    public async Task<string> WriteAsync(
        [Description("Provider id from vault_list (1password or bitwarden).")] string providerId,
        [Description("Item name, without any secret value.")] string name,
        [Description("command or generate; generate never exposes the generated value to the agent.")] string source,
        [Description("For command only: executable and separate arguments; stdout is captured privately as the value. Each output stream is capped at 65,536 characters; overflow terminates the command and refuses the write. Never embed a secret in argv.")] string[]? argv = null,
        [Description("Existing reference from vault_list to update; omit to create a new item.")] string? reference = null,
        [Description("For generate: number of characters, from 1 to 1024; default 32.")] int length = 32,
        [Description("For generate: distinct printable ASCII characters to choose from, at least two; default letters and digits.")] string characterSet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
        [Description("For command: predeclare 1–64 nonsecret field names. Private stdout must be a JSON object with exactly these names and string values. Sets those fields in one provider update and preserves other fields; no literal values are accepted as tool parameters.")] string[]? fieldNames = null,
        CancellationToken cancellationToken = default)
    {
        if (await _UnavailableAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }

        if (_CallerSession() is not { WorkingDirectory: { Length: > 0 } directory } session)
        {
            return _Error("vault_write needs a live session with a working directory.");
        }

        IVaultProvider? provider = _hub.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null || !provider.CanWrite)
        {
            return _Error("Agent writes are disabled for this provider. Enable Allow agents to create and update items in its Options page.");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            return _Error("Provide an item name without control characters.");
        }

        if (source is not ("command" or "generate") || (fieldNames is not null && (source != "command" || fieldNames.Length is < 1 or > 64
                || fieldNames.Distinct(StringComparer.Ordinal).Count() != fieldNames.Length
                || fieldNames.Any(field => field is not { Length: > 0 and <= 64 } || !char.IsAsciiLetter(field[0])
                    || field.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))))
            || (source == "command" && argv is not [{ Length: > 0 }, ..])
            || (source == "generate" && (argv is not null || length is < 1 or > 1024
                || characterSet is not { Length: >= 2 and <= 94 }
                || characterSet.Any(character => character is < '!' or > '~') || characterSet.Distinct().Count() != characterSet.Length)))
        {
            return _Error("Choose command with argv, or generate without argv, length 1–1024 and at least two distinct printable ASCII characters.");
        }

        try
        {
            VaultItemVersion? expectedVersion = null;
            if (reference is not null)
            {
                IReadOnlyList<VaultItem> items = await provider.ListItemsAsync(cancellationToken).ConfigureAwait(false);
                if (items.FirstOrDefault(item => item.Reference == reference) is not { } existing)
                {
                    return _Error("The update reference is not offered by this provider; see vault_list.");
                }

                name = existing.DisplayName;
                expectedVersion = await _VersionAsync(provider, reference, cancellationToken).ConfigureAwait(false)
                    ?? throw new VaultWriteConflictException();
            }

            using IDisposable destinationLock = await _hub.LockWriteAsync(provider.Id, expectedVersion?.ItemId ?? "new:" + name, cancellationToken).ConfigureAwait(false);
            if (reference is not null && expectedVersion != await _VersionAsync(provider, reference, cancellationToken).ConfigureAwait(false))
            {
                throw new VaultWriteConflictException();
            }

            string value;
            if (source == "generate")
            {
                value = RandomNumberGenerator.GetString(characterSet, length);
            }
            else if (argv is [{ Length: > 0 }, ..])
            {
                VaultLease lease = _hub.Lease();
                ContainedRun? contained = null;
                if (!_hub.TryStart(lease, () => provider.CanWrite && _CallerSession()?.PaneId == session.PaneId && !cancellationToken.IsCancellationRequested,
                    () => contained = CommandRunnerProcess.StartContained(argv[0], argv[1..], directory, new Dictionary<string, string>())) || contained is null)
                {
                    return _Error("Write refused: the vault closed, the setting changed or the session ended.");
                }

                var run = await _WaitAsync(contained, TimeSpan.FromSeconds(MaxTimeoutSeconds), cancellationToken, stdoutOnly: true).ConfigureAwait(false);
                if (run.OutputLimitExceeded)
                {
                    return _Error("Command source exceeded 65,536 characters on stdout or stderr; nothing was written.");
                }
                if (run.TimedOut || run.ExitCode != 0 || run.Output.Length == 0)
                {
                    return _Error("The value command failed, timed out or returned no value; nothing was written.");
                }

                value = run.Output;
            }
            else
            {
                return _Error("The command source requires argv.");
            }

            if (await _UnavailableAsync(cancellationToken).ConfigureAwait(false) is not null
                || !provider.CanWrite || _CallerSession()?.PaneId != session.PaneId)
            {
                return _Error("Write refused: the vault closed, the setting changed or the session ended.");
            }

            if (reference is not null && expectedVersion != await _VersionAsync(provider, reference, cancellationToken).ConfigureAwait(false))
            {
                throw new VaultWriteConflictException();
            }

            if (!await _hub.AuditAsync("WriteRequested", session.PaneId, name, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return _Error("Write refused: the audit could not be stored.");
            }

            if (fieldNames is not null)
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || document.RootElement.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
                {
                    return _Error("Expected a JSON object with distinct field names.");
                }

                Dictionary<string, string>? fields = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
                if (fields is null || !fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(fieldNames) || fields.Values.Any(value => value is null))
                {
                    return _Error("Command stdout must contain exactly the predeclared fields, each with a string value.");
                }

                if (reference is not null && expectedVersion is not null)
                {
                    await provider.WriteFieldsAsync(name, reference, fields, expectedVersion, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await provider.WriteFieldsAsync(name, reference, fields, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (reference is not null && expectedVersion is not null)
                {
                    await provider.WriteAsync(name, reference, value, expectedVersion, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await provider.WriteAsync(name, reference, value, cancellationToken).ConfigureAwait(false);
                }
            }
            await _hub.AuditAsync(reference is null ? "Created" : "Updated", session.PaneId, name, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return _Serialize(new { ok = true, value = "[vault: stored]" });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Vault write through '{ProviderId}' failed ({ExceptionType}).", provider.Id, exception.GetType().Name);
            await _hub.AuditAsync("WriteFailed", session.PaneId, name, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return _Error(exception is VaultWriteConflictException ? "The target revision changed; no vault update was made. Retain the staging value."
                : exception is UnauthorizedAccessException
                ? "Write refused: the provider setting is off or the account does not have create/update permission."
                : "Write failed or was refused by the provider. Check the connection and the account's create/update permissions; no value or provider output is returned.");
        }
    }

    // Longest value first, so a value that contains another is replaced whole.
    internal static (string Output, int Masked) Redact(string output, IEnumerable<(string Value, string Name)> values)
    {
        var masked = 0;
        IEnumerable<(string Value, string Name)> forms = values.Where(value => value.Value.Length > 0).SelectMany(value =>
        {
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Value));
            string wrapped = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Value), Base64FormattingOptions.InsertLineBreaks);
            string base64Url = base64.Replace('+', '-').Replace('/', '_');
            string uri = Uri.EscapeDataString(value.Value);
            string form = System.Net.WebUtility.UrlEncode(value.Value);
            return new[]
            {
                value, (base64, value.Name), (base64.TrimEnd('='), value.Name), (base64Url, value.Name), (base64Url.TrimEnd('='), value.Name),
                (wrapped, value.Name), (wrapped.Replace("\r\n", "\n"), value.Name), (uri, value.Name), (form, value.Name),
                (Regex.Replace(uri, "%[0-9A-F]{2}", match => match.Value.ToLowerInvariant()), value.Name),
                (Regex.Replace(form, "%[0-9A-F]{2}", match => match.Value.ToLowerInvariant()), value.Name),
            };
        });
        foreach (var (value, name) in forms.Distinct().OrderByDescending(v => v.Value.Length))
        {
            var parts = output.Split(value);
            masked += parts.Length - 1;
            output = string.Join($"[vault: {name}]", parts);
        }

        return (output, masked);
    }

    private async Task<bool> _ConsentAsync(
        VaultLease lease, ISessionHandle session, string directory, string[] argv, IReadOnlyList<VaultGrant> grants, List<(string Variable, string Reference, string Name)> pairs, string reason,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (sessionGrants, projectGrants) = await _hub.GrantsForAsync(session.PaneId, session.ProjectId, cancellationToken).ConfigureAwait(false);
        var keys = grants.Select(VaultHub.GrantKey).ToList();
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
                if (!_hub.TryGrantSession(lease, session.PaneId, keys))
                {
                    return false;
                }

                break;
            case Project when session.ProjectId is { } project:
                if (!await _hub.TryGrantProjectAsync(lease, session.PaneId, project, keys, cancellationToken).ConfigureAwait(false))
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

    // AC-2333 S5: `run` holds everything the command started; disposing it ends what is left, also children that outlived
    // the command itself, before the output is drained, so nothing with the secrets in its environment survives the call.
    private static async Task<(int ExitCode, string Output, bool TimedOut, bool InputSent, bool OutputLimitExceeded)> _WaitAsync(
        ContainedRun run, TimeSpan timeout, CancellationToken cancellationToken, bool stdoutOnly = false, string? input = null, bool stdinNewline = true)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var exceeded = 0;
        void LimitReached()
        {
            Interlocked.Exchange(ref exceeded, 1);
            timeoutSource.Cancel();
        }

        var readOutput = _ReadBoundedAsync(run.Output, LimitReached, timeoutSource.Token);
        var readError = _ReadBoundedAsync(run.Error, LimitReached, timeoutSource.Token);
        Task writeInput = input is null ? Task.CompletedTask : _WriteInputAsync(run.Input, input, stdinNewline);
        var timedOut = false;
        int? exitCode;
        using (run)
        {
            try
            {
                await run.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = Volatile.Read(ref exceeded) == 0;
            }

            exitCode = run.ExitCode;
        }

        var output = await CommandRunnerProcess._DrainAsync(readOutput).ConfigureAwait(false);
        var error = await CommandRunnerProcess._DrainAsync(readError).ConfigureAwait(false);
        if (!stdoutOnly)
        {
            output += error;
        }
        run.Output.Dispose();
        run.Error.Dispose();
        var inputSent = true;
        try
        {
            await writeInput.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            inputSent = false;
        }

        bool capped = Volatile.Read(ref exceeded) != 0;
        return (timedOut || !inputSent || capped ? -1 : exitCode ?? -1, capped ? string.Empty : output, timedOut, inputSent, capped);
    }

    private static async Task<string> _ReadBoundedAsync(StreamReader reader, Action limitReached, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length > OutputLimit - count)
            {
                limitReached();
                return string.Empty;
            }

            output.Append(buffer, 0, count);
        }

        return output.ToString();
    }

    private static async Task _WriteInputAsync(StreamWriter? input, string value, bool appendNewline)
    {
        if (input is null)
        {
            throw new InvalidOperationException("The command's stdin is unavailable.");
        }

        await using (input.ConfigureAwait(false))
        {
            await input.WriteAsync(appendNewline ? value + "\n" : value).ConfigureAwait(false);
        }
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

    private async Task<VaultItemVersion?> _VersionAsync(IVaultProvider provider, string reference, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetVersionAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Vault provider revision check failed ({ExceptionType}).", exception.GetType().Name);
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
