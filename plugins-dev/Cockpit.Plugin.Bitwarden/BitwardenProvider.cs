using System.Text.Json;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.Bitwarden;

// The Bitwarden side of the host's vault hub (AC-2349): one machine account that reads exactly one Secrets Manager project.
internal sealed class BitwardenProvider(BitwardenCli cli) : IVaultProvider
{
    private const string Prefix = "bws://";

    private IVaultProviderContext _context = UnavailableVaultProviderContext.Instance;

    public string Id => "bitwarden";

    public string DisplayName => "Bitwarden";

    public string ReferenceScheme => "bws";

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

    // Reference and name only: the list answer also carries every value, and ParseSecrets copies out id and key, never the value.
    public async Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new BitwardenException("not configured");
        var secrets = BitwardenCli.ParseSecrets(await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false));
        return [.. secrets.Select(secret => new VaultItem($"{Prefix}{connection.ProjectName}/{secret.Name}", secret.Name))];
    }

    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var connection = _Connection() ?? throw new BitwardenException("not configured");
        if (SecretNameIn(reference, connection) is not { } name)
        {
            throw new BitwardenException("reference outside the connected project");
        }

        var matches = BitwardenCli.ParseSecrets(await cli.RunAsync(connection.Token, ["secret", "list", connection.ProjectId], cancellationToken).ConfigureAwait(false))
            .Where(secret => secret.Name == name).ToList();
        if (matches.Count != 1)
        {
            throw new BitwardenException(matches.Count == 0 ? "secret not found" : "secret name is not unique");
        }

        return BitwardenCli.ParseValue(await cli.RunAsync(connection.Token, ["secret", "get", matches[0].Id], cancellationToken).ConfigureAwait(false));
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
