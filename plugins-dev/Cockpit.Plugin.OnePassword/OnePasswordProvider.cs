using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.OnePassword;

// The 1Password side of the host's vault hub (AC-2348): one service account that reads exactly one vault.
internal sealed class OnePasswordProvider(OnePasswordCli cli, Func<bool> allowAgentWrites) : IVaultProvider
{
    private const int ItemLimit = 200;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private IVaultProviderContext _context = UnavailableVaultProviderContext.Instance;

    public string Id => "1password";

    public string DisplayName => "1Password";

    public string ReferenceScheme => "op";

    public bool CanWrite => allowAgentWrites() && Availability == VaultAvailability.Available;

    public Task WriteAsync(string name, string? reference, string value, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, null, value, cancellationToken);

    public Task WriteAsync(string name, string reference, string value, VaultItemVersion expected, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, null, value, cancellationToken, expected);

    public Task WriteFieldsAsync(string name, string? reference, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, fields, null, cancellationToken);

    public Task WriteFieldsAsync(string name, string reference, IReadOnlyDictionary<string, string> fields, VaultItemVersion expected, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, fields, null, cancellationToken, expected);

    public async Task<VaultItemVersion> GetVersionAsync(string reference, CancellationToken cancellationToken)
    {
        Connection connection = _Connection() ?? throw new OnePasswordException("not configured");
        if (!InVault(reference, connection))
        {
            throw new OnePasswordException("reference outside the connected vault");
        }

        JsonObject item = JsonNode.Parse(await cli.RunAsync(connection.Token,
            ["item", "get", reference["op://".Length..].Split('/')[1], "--vault", connection.VaultId, "--format", "json"], cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new OnePasswordException("unexpected response");
        return _Version(connection, item);
    }

    private static VaultItemVersion _Version(Connection connection, JsonObject item) => new(
        connection.VaultId + "/" + (item["id"]?.GetValue<string>() ?? throw new OnePasswordException("item identity missing")),
        item["version"]?.ToJsonString() ?? throw new OnePasswordException("item revision missing"));

    private async Task _WriteAsync(string name, string? reference, IReadOnlyDictionary<string, string>? changes, string? value, CancellationToken cancellationToken, VaultItemVersion? expected = null)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanWrite)
            {
                throw new UnauthorizedAccessException("Agent writes are disabled.");
            }

            Connection connection = _Connection() ?? throw new OnePasswordException("not configured");
            JsonObject item;
            string[] arguments;
            if (reference is null)
            {
                if (OnePasswordCli.ParseItems(await cli.RunAsync(connection.Token, ["item", "list", "--vault", connection.VaultId, "--format", "json"], cancellationToken).ConfigureAwait(false)).Any(item => item.Name == name))
                {
                    throw new OnePasswordException("item already exists; use its reference to update");
                }

                item = new JsonObject
                {
                    ["title"] = name,
                    ["category"] = "PASSWORD",
                    ["fields"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "password", ["label"] = "password", ["type"] = "CONCEALED", ["purpose"] = "PASSWORD", ["value"] = value,
                    }),
                };
                if (changes is not null)
                {
                    item["category"] = "SECURE_NOTE";
                    item["fields"] = new JsonArray(changes.Select(field => (JsonNode)new JsonObject
                    {
                        ["label"] = field.Key, ["type"] = "CONCEALED", ["value"] = field.Value,
                    }).ToArray());
                }

                arguments = ["item", "create", "-", "--vault", connection.VaultId, "--format", "json"];
            }
            else
            {
                if (!InVault(reference, connection))
                {
                    throw new OnePasswordException("reference outside the connected vault");
                }

                string[] segments = reference["op://".Length..].Split('/');
                if (segments.Length is not (3 or 4))
                {
                    throw new OnePasswordException("invalid field reference");
                }

                item = JsonNode.Parse(await cli.RunAsync(connection.Token,
                    ["item", "get", segments[1], "--vault", connection.VaultId, "--format", "json", "--reveal"], cancellationToken).ConfigureAwait(false))?.AsObject()
                    ?? throw new OnePasswordException("unexpected response");
                if (expected is not null && _Version(connection, item) != expected)
                {
                    throw new VaultWriteConflictException();
                }
                var fields = item["fields"]?.AsArray().OfType<JsonObject>().Where(field =>
                    (field["id"]?.GetValue<string>() == segments[^1] || field["label"]?.GetValue<string>() == segments[^1])
                    && (segments.Length == 3 ? field["section"] is null
                        : field["section"]?["id"]?.GetValue<string>() == segments[2] || field["section"]?["label"]?.GetValue<string>() == segments[2])).ToList();
                if (changes is null && (fields is not [{ } target] || target["type"]?.GetValue<string>() != "CONCEALED"))
                {
                    throw new OnePasswordException("concealed field not found or not unique");
                }

                if (changes is null)
                {
                    JsonObject selected = fields?.Single() ?? throw new OnePasswordException("field missing");
                    selected["value"] = value;
                }
                else
                {
                    JsonArray allFields = item["fields"]?.AsArray() ?? throw new OnePasswordException("fields missing");
                    foreach (var change in changes)
                    {
                        var matches = allFields.OfType<JsonObject>().Where(field => field["section"] is null
                            && (field["id"]?.GetValue<string>() == change.Key || field["label"]?.GetValue<string>() == change.Key)).ToList();
                        if (matches.Count > 1 || matches.Any(field => field["type"]?.GetValue<string>() is not ("CONCEALED" or "STRING")))
                        {
                            throw new OnePasswordException("field is ambiguous or not a text field");
                        }

                        if (matches.Count == 0)
                        {
                            allFields.Add(new JsonObject { ["label"] = change.Key, ["type"] = "CONCEALED", ["value"] = change.Value });
                        }
                        else
                        {
                            matches[0]["value"] = change.Value;
                        }
                    }
                }
                arguments = ["item", "edit", item["id"]?.GetValue<string>() ?? throw new OnePasswordException("item id missing"), "--vault", connection.VaultId, "--format", "json"];
            }

            if (!CanWrite)
            {
                throw new UnauthorizedAccessException("Agent writes are disabled.");
            }

            try
            {
                await cli.RunAsync(connection.Token, arguments, cancellationToken, item.ToJsonString()).ConfigureAwait(false);
            }
            catch (OnePasswordException exception) when (exception.Reason == "write permission denied")
            {
                throw new UnauthorizedAccessException("The account does not permit writing.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Bind(IVaultProviderContext context) => _context = context;

    public VaultAvailability Availability => _context.Availability;

    public void OpenEncryptionSettings() => _context.OpenEncryptionSettings();

    public event EventHandler? AvailabilityChanged
    {
        add => _context.AvailabilityChanged += value;
        remove => _context.AvailabilityChanged -= value;
    }

    public async Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_Connection() is not { } connection)
        {
            return new VaultProviderStatus(VaultProviderState.NotConfigured);
        }

        try
        {
            var vaults = OnePasswordCli.ParseVaults(await cli.RunAsync(connection.Token, ["vault", "list", "--format", "json"], cancellationToken).ConfigureAwait(false));
            return vaults is [{ } only] && only.Id == connection.VaultId
                ? new VaultProviderStatus(VaultProviderState.Connected, "service account", $"reads one vault: {connection.VaultName}")
                : new VaultProviderStatus(VaultProviderState.Error, Error: "token reads other vaults");
        }
        catch (OnePasswordException exception)
        {
            return new VaultProviderStatus(VaultProviderState.Error, Error: exception.Reason);
        }
    }

    public async Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new OnePasswordException("not configured");
        var items = OnePasswordCli.ParseItems(await cli.RunAsync(connection.Token, ["item", "list", "--vault", connection.VaultId, "--format", "json"], cancellationToken).ConfigureAwait(false));
        var result = new List<VaultItem>();
        foreach (var (id, title) in items.Take(ItemLimit))
        {
            var json = await cli.RunAsync(connection.Token, ["item", "get", id, "--vault", connection.VaultId, "--format", "json"], cancellationToken).ConfigureAwait(false);
            result.AddRange(TextFields(title, json));
        }

        return result;
    }

    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new OnePasswordException("not configured");
        if (!InVault(reference, connection))
        {
            throw new OnePasswordException("reference outside the connected vault");
        }

        return await cli.RunAsync(connection.Token, ["read", "--no-newline", reference], cancellationToken).ConfigureAwait(false);
    }

    // What the token can read, with the item count of each vault. Stores nothing.
    public async Task<TestReply> TestAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var vaults = new List<VaultInfo>();
            foreach (var (id, name) in OnePasswordCli.ParseVaults(await cli.RunAsync(token, ["vault", "list", "--format", "json"], cancellationToken).ConfigureAwait(false)))
            {
                var count = OnePasswordCli.ParseItems(await cli.RunAsync(token, ["item", "list", "--vault", id, "--format", "json"], cancellationToken).ConfigureAwait(false)).Count;
                vaults.Add(new VaultInfo(name, count));
            }

            return new TestReply(true, null, vaults);
        }
        catch (OnePasswordException exception)
        {
            return new TestReply(false, exception.Reason, []);
        }
    }

    // Stores the token only when it reads exactly one vault; the page offers no way around that.
    public async Task<StatusReply> ConnectAsync(string token, CancellationToken cancellationToken)
    {
        var availability = _context.Availability;
        if (availability != VaultAvailability.Available)
        {
            return new StatusReply(availability.ToString(), "NotConfigured", null, null);
        }

        try
        {
            var vaults = OnePasswordCli.ParseVaults(await cli.RunAsync(token, ["vault", "list", "--format", "json"], cancellationToken).ConfigureAwait(false));
            if (vaults is not [{ } only])
            {
                return new StatusReply(availability.ToString(), "Error", null, vaults.Count == 0 ? "token reads no vault" : "token reads more than one vault");
            }

            var stored = _context.TryStoreCredential(JsonSerializer.Serialize(new Connection(token, only.Id, only.Name)));
            return stored
                ? new StatusReply(availability.ToString(), "Connected", only.Name, null)
                : new StatusReply(_context.Availability.ToString(), "NotConfigured", null, null);
        }
        catch (OnePasswordException exception)
        {
            return new StatusReply(availability.ToString(), "Error", null, exception.Reason);
        }
    }

    public void Disconnect() => _context.ClearCredential();

    public async Task<StatusReply> StatusAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var vault = status.State == VaultProviderState.Connected ? _Connection()?.VaultName : null;
        return new StatusReply(_context.Availability.ToString(), status.State.ToString(), vault, status.Error);
    }

    // True when the reference is op://<vault>/<item>/… and the vault segment is the connected vault, by name or id.
    internal static bool InVault(string reference, Connection connection)
    {
        const string Prefix = "op://";
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal) || reference.Any(char.IsControl))
        {
            return false;
        }

        var segments = reference[Prefix.Length..].Split('/');
        return segments.Length >= 3
            && segments.All(segment => segment.Length > 0)
            && (string.Equals(segments[0], connection.VaultName, StringComparison.OrdinalIgnoreCase) || segments[0] == connection.VaultId);
    }

    // Text-field references and labels only; values stay inside the provider.
    internal static IEnumerable<VaultItem> TextFields(string title, string itemJson)
    {
        using var document = JsonDocument.Parse(itemJson);
        if (!document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<VaultItem>();
        foreach (var field in fields.EnumerateArray())
        {
            var textField = field.TryGetProperty("type", out var type) && type.GetString() is "CONCEALED" or "STRING";
            if (textField && field.TryGetProperty("reference", out var reference) && reference.GetString() is { Length: > 0 } text)
            {
                var label = field.TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
                found.Add(new VaultItem(text, string.IsNullOrEmpty(label) ? title : $"{title} · {label}"));
            }
        }

        return found;
    }

    private Connection? _Connection()
    {
        if (_context.ReadCredential() is not { Length: > 0 } stored)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Connection>(stored) is { Token.Length: > 0 } connection ? connection : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record Connection(string Token, string VaultId, string VaultName);
}
