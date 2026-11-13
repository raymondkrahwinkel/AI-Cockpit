using System.Text.Json;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.OnePassword;

// The 1Password side of the host's vault hub (AC-2348): one service account that reads exactly one vault.
internal sealed class OnePasswordProvider(OnePasswordCli cli) : IVaultProvider
{
    private const int ItemLimit = 200;

    private IVaultProviderContext _context = UnavailableVaultProviderContext.Instance;

    public string Id => "1password";

    public string DisplayName => "1Password";

    public string ReferenceScheme => "op";

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
            result.AddRange(ConcealedFields(title, json));
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

    // The concealed fields of one `op item get --format json`: reference and label only; the values in it are never read.
    internal static IEnumerable<VaultItem> ConcealedFields(string title, string itemJson)
    {
        using var document = JsonDocument.Parse(itemJson);
        if (!document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<VaultItem>();
        foreach (var field in fields.EnumerateArray())
        {
            var concealed = field.TryGetProperty("type", out var type) && type.GetString() == "CONCEALED";
            if (concealed && field.TryGetProperty("reference", out var reference) && reference.GetString() is { Length: > 0 } text)
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
