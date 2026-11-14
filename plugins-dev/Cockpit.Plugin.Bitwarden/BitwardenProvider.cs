using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.Bitwarden;

// The Bitwarden side of the host's vault hub (AC-2349): one machine account that reads exactly one Secrets Manager project.
internal sealed class BitwardenProvider(BitwardenCli cli, Func<bool> allowAgentWrites) : IVaultProvider
{
    private const string Prefix = "bws://";
    internal const int FieldLimit = 64;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private IVaultProviderContext _context = UnavailableVaultProviderContext.Instance;

    public string Id => "bitwarden";

    public string DisplayName => "Bitwarden";

    public string ReferenceScheme => "bws";

    public bool CanWrite => allowAgentWrites() && Availability == VaultAvailability.Available;

    public Task WriteAsync(string name, string? reference, string value, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, value, null, cancellationToken);

    public Task WriteAsync(string name, string reference, string value, VaultItemVersion expected, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, value, null, cancellationToken, expected);

    public Task WriteFieldsAsync(string name, string? reference, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, null, fields, cancellationToken);

    public Task WriteFieldsAsync(string name, string reference, IReadOnlyDictionary<string, string> fields, VaultItemVersion expected, CancellationToken cancellationToken) =>
        _WriteAsync(name, reference, null, fields, cancellationToken, expected);

    public async Task<VaultItemVersion> GetVersionAsync(string reference, CancellationToken cancellationToken)
    {
        Connection connection = _Connection() ?? throw new BitwardenException("not configured");
        var secrets = BitwardenCli.ParseSecrets(await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false));
        var (id, _) = _Target(reference, connection, secrets);
        using var document = JsonDocument.Parse(await cli.RunAsync(connection.Token, ["secret", "get", id], cancellationToken).ConfigureAwait(false));
        return _Version(connection, document.RootElement);
    }

    private static VaultItemVersion _Version(Connection connection, JsonElement item)
    {
        string revision = item.GetProperty("revisionDate").GetString() ?? throw new BitwardenException("item revision missing");
        string value = item.GetProperty("value").GetString() ?? throw new BitwardenException("item value missing");
        string? note = item.TryGetProperty("note", out var metadata) ? metadata.GetString() : null;
        return new VaultItemVersion(connection.ProjectId + "/" + item.GetProperty("id").GetString(),
            revision + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value + "\0" + note))));
    }

    private async Task _WriteAsync(string name, string? reference, string? value, IReadOnlyDictionary<string, string>? changes, CancellationToken cancellationToken, VaultItemVersion? expected = null)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanWrite)
            {
                throw new UnauthorizedAccessException("Agent writes are disabled.");
            }

            Connection connection = _Connection() ?? throw new BitwardenException("not configured");
            var secrets = BitwardenCli.ParseSecrets(await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false));
            if (reference is null && secrets.Any(secret => secret.Name == name))
            {
                throw new BitwardenException("item already exists");
            }

            string? id = null;
            string? field = null;
            Dictionary<string, string>? fields = null;
            if (reference is not null)
            {
                (id, field) = _Target(reference, connection, secrets);
                string currentJson = await cli.RunAsync(connection.Token, ["secret", "get", id], cancellationToken).ConfigureAwait(false);
                using var current = JsonDocument.Parse(currentJson);
                if (expected is not null && _Version(connection, current.RootElement) != expected)
                {
                    throw new VaultWriteConflictException();
                }
                fields = _Fields(BitwardenCli.ParseValue(currentJson), _DeclaredFields(current.RootElement));
                if ((changes is not null || field is not null) && fields is null)
                {
                    throw new BitwardenException("item is not a field bundle");
                }

                if (fields is not null && changes is null && (field is null || !fields.ContainsKey(field)))
                {
                    throw new BitwardenException("choose an existing field reference");
                }
            }

            if (changes is not null)
            {
                fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
                MergeFields(fields, changes);
            }
            else if (fields is not null && field is not null && value is not null)
            {
                fields[field] = value;
            }

            string storedValue = fields is null ? value ?? throw new BitwardenException("value missing")
                : JsonSerializer.Serialize(new Dictionary<string, object> { ["$cockpitFields"] = 1, ["fields"] = fields });

            if (!CanWrite)
            {
                throw new UnauthorizedAccessException("Agent writes are disabled.");
            }

            var arguments = new List<string> { "secret", reference is null ? "create" : "edit" };
            if (changes is not null && fields is not null)
            {
                arguments.AddRange(["--note", JsonSerializer.Serialize(new { cockpitVaultBundleVersion = 1, fieldNames = fields.Keys.ToArray() })]);
            }

            arguments.AddRange(reference is null ? ["--", name, storedValue, connection.ProjectId]
                : [id ?? throw new BitwardenException("item missing"), "--value=" + storedValue]);
            try
            {
                await cli.RunAsync(connection.Token, arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (BitwardenException exception) when (exception.Reason == "write permission denied")
            {
                throw new UnauthorizedAccessException("The account does not permit writing.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal static void MergeFields(Dictionary<string, string> fields, IReadOnlyDictionary<string, string> changes)
    {
        if (fields.Keys.Concat(changes.Keys).Distinct(StringComparer.Ordinal).Count() > FieldLimit)
        {
            throw new BitwardenException("field bundle exceeds the supported field limit");
        }

        foreach (var change in changes)
        {
            fields[change.Key] = change.Value;
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
            var projects = BitwardenCli.ParseProjects(await cli.RunAsync(connection.Token, ["project", "list"], cancellationToken).ConfigureAwait(false));
            return projects is [{ } only] && only.Id == connection.ProjectId
                ? new VaultProviderStatus(VaultProviderState.Connected, "machine account", $"reads one project: {connection.ProjectName}")
                : new VaultProviderStatus(VaultProviderState.Error, Error: "token reads other projects");
        }
        catch (BitwardenException exception)
        {
            return new VaultProviderStatus(VaultProviderState.Error, Error: exception.Reason);
        }
    }

    // Only the multi-field writer sets the public note flag; scalar contents never define metadata.
    public async Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new BitwardenException("not configured");
        string json = await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false);
        return ItemsIn(json, connection);
    }

    internal static IReadOnlyList<VaultItem> ItemsIn(string json, Connection connection)
    {
        var secrets = BitwardenCli.ParseSecrets(json);
        using var document = JsonDocument.Parse(json);
        var result = new List<VaultItem>();
        foreach (var secret in secrets)
        {
            JsonElement element = document.RootElement.EnumerateArray().First(item => item.GetProperty("id").GetString() == secret.Id);
            string[]? fields = _DeclaredFields(element);
            string reference = $"{Prefix}{connection.ProjectName}/{secret.Name}";
            if (fields is null)
            {
                result.Add(new VaultItem(reference, secret.Name));
            }
            else
            {
                result.AddRange(fields.Select(key => new VaultItem(reference + "#field=" + Uri.EscapeDataString(key), secret.Name + " · " + key)));
            }
        }

        if (result.Select(item => item.Reference).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw new BitwardenException("item or field references are ambiguous");
        }

        return result;
    }

    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new BitwardenException("not configured");
        var secrets = BitwardenCli.ParseSecrets(await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false));
        var (id, field) = _Target(reference, connection, secrets);
        string json = await cli.RunAsync(connection.Token, ["secret", "get", id], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        string stored = BitwardenCli.ParseValue(json);
        return field is null ? stored : _Fields(stored, _DeclaredFields(document.RootElement))?.GetValueOrDefault(field) ?? throw new BitwardenException("field missing");
    }

    private static string[]? _DeclaredFields(JsonElement item)
    {
        if (!item.TryGetProperty("note", out var note) || note.ValueKind != JsonValueKind.String || note.GetString() is not { } text)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("cockpitVaultBundleVersion", out var version) || version.GetInt32() != 1)
            {
                return null;
            }

            string[]? names = document.RootElement.GetProperty("fieldNames").Deserialize<string[]>();
            return names is { Length: > 0 and <= FieldLimit } && names.Distinct(StringComparer.Ordinal).Count() == names.Length
                && names.All(name => name is { Length: > 0 and <= 64 } && char.IsAsciiLetter(name[0])
                    && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) ? names : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? _Fields(string value, string[]? declared)
    {
        if (declared is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            Dictionary<string, string>? result = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("$cockpitFields", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1
                && document.RootElement.TryGetProperty("fields", out var fields)
                ? fields.Deserialize<Dictionary<string, string>>() : null;
            return result is not null && result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(declared)
                ? result : throw new BitwardenException("bundle does not match declared field metadata");
        }
        catch (JsonException)
        {
            throw new BitwardenException("invalid declared field bundle");
        }
    }

    private static (string Id, string? Field) _Target(string reference, Connection connection, IReadOnlyList<(string Id, string Name)> secrets)
    {
        string name = SecretNameIn(reference, connection) ?? throw new BitwardenException("reference outside the connected project");
        string? field = null;
        var matches = secrets.Where(secret => secret.Name == name).ToList();
        int separator = name.LastIndexOf("#field=", StringComparison.Ordinal);
        if (matches.Count == 0 && separator >= 0)
        {
            field = Uri.UnescapeDataString(name[(separator + 7)..]);
            matches = secrets.Where(secret => secret.Name == name[..separator]).ToList();
        }

        return matches is [{ } only] ? (only.Id, field) : throw new BitwardenException("item missing or not unique");
    }

    // What the token can read, with the secret count of each project. Stores nothing.
    public async Task<TestReply> TestAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var projects = new List<ProjectInfo>();
            foreach (var (id, name) in BitwardenCli.ParseProjects(await cli.RunAsync(token, ["project", "list"], cancellationToken).ConfigureAwait(false)))
            {
                var count = BitwardenCli.ParseSecrets(await cli.RunAsync(token, ["secret", "list", id], cancellationToken).ConfigureAwait(false)).Count;
                projects.Add(new ProjectInfo(name, count));
            }

            return new TestReply(true, null, projects);
        }
        catch (BitwardenException exception)
        {
            return new TestReply(false, exception.Reason, []);
        }
    }

    // Stores the token only when it reads exactly one project; the page offers no way around that.
    public async Task<StatusReply> ConnectAsync(string token, CancellationToken cancellationToken)
    {
        var availability = _context.Availability;
        if (availability != VaultAvailability.Available)
        {
            return new StatusReply(availability.ToString(), "NotConfigured", null, null);
        }

        try
        {
            var projects = BitwardenCli.ParseProjects(await cli.RunAsync(token, ["project", "list"], cancellationToken).ConfigureAwait(false));
            if (projects is not [{ } only])
            {
                return new StatusReply(availability.ToString(), "Error", null, projects.Count == 0 ? "token reads no project" : "token reads more than one project");
            }

            var stored = _context.TryStoreCredential(JsonSerializer.Serialize(new Connection(token, only.Id, only.Name)));
            return stored
                ? new StatusReply(availability.ToString(), "Connected", only.Name, null)
                : new StatusReply(_context.Availability.ToString(), "NotConfigured", null, null);
        }
        catch (BitwardenException exception)
        {
            return new StatusReply(availability.ToString(), "Error", null, exception.Reason);
        }
    }

    public void Disconnect() => _context.ClearCredential();

    public async Task<StatusReply> StatusAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var project = status.State == VaultProviderState.Connected ? _Connection()?.ProjectName : null;
        return new StatusReply(_context.Availability.ToString(), status.State.ToString(), project, status.Error);
    }

    // The secret name of bws://<project>/<secret-name> when the project segment is the connected project (name or id); else null.
    internal static string? SecretNameIn(string reference, Connection connection)
    {
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal) || reference.Any(char.IsControl))
        {
            return null;
        }

        var rest = reference[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return null;
        }

        var project = rest[..slash];
        return string.Equals(project, connection.ProjectName, StringComparison.OrdinalIgnoreCase) || project == connection.ProjectId
            ? rest[(slash + 1)..]
            : null;
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

    internal sealed record Connection(string Token, string ProjectId, string ProjectName);
}
