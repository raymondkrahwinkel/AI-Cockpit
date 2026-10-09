using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// How one Depot connection names its projects: `org/slug` once its list_projects rows carry an `address` (Depot E1), a bare slug before.
internal sealed class DepotProjectAddressing
{
    private static readonly ConditionalWeakTable<ICockpitHost, ConcurrentDictionary<string, DepotProjectAddressing>> _instances = new();

    private readonly ICockpitHost _host;
    private readonly DepotConnectionRegistration _connection;
    private readonly string _scheme;
    private IReadOnlyList<DepotMemorySource.ListedProject> _rows = [];
    private int _upgradeStarted;

    private DepotProjectAddressing(ICockpitHost host, DepotConnectionRegistration connection, string scheme)
    {
        _host = host;
        _connection = connection;
        _scheme = scheme;
    }

    public static DepotProjectAddressing For(ICockpitHost host, DepotConnectionRegistration connection, string scheme) =>
        _instances.GetOrCreateValue(host).GetOrAdd(
            $"{connection.Id}|{scheme}|{connection.Url}", _ => new DepotProjectAddressing(host, connection, scheme));

    private bool Addressed => _rows.Any(row => row.Address is not null);

    // Remembers the latest list_projects rows and, at the first sight of addresses, upgrades the bare references stored under this scheme.
    public async Task ObserveAsync(IReadOnlyList<DepotMemorySource.ListedProject> rows, CancellationToken cancellationToken)
    {
        _rows = rows;
        if (Addressed && Interlocked.Exchange(ref _upgradeStarted, 1) == 0)
        {
            try
            {
                await _UpgradeStoredReferencesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interlocked.Exchange(ref _upgradeStarted, 0);
                _host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot")
                    .LogWarning(exception, "Depot connection '{Connection}': upgrading stored project references failed", _connection.Name);
            }
        }
    }

    // The `project` argument for a stored reference or shared-id value: the address on an addressed Depot, the bare slug on an older one.
    public async Task<string> ProjectArgumentAsync(string value, CancellationToken cancellationToken)
    {
        if (_rows.Count == 0)
        {
            await _LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return Addressed ? Upgraded(value) ?? value : value[(value.LastIndexOf('/') + 1)..];
    }

    // value (a bare slug, optionally followed by /path) with its slug replaced by the address; null when already qualified, unmatched or ambiguous.
    private string? Upgraded(string value) => _Upgraded(value, _rows);

    // A `<scheme>:<bare slug>` reference with its address where one exists; anything else unchanged.
    public string UpgradeReference(string reference)
    {
        var prefix = $"{_scheme}:";
        return reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Upgraded(reference[prefix.Length..]) is { } address
            ? prefix + address
            : reference;
    }

    private static string? _Upgraded(string value, IReadOnlyList<DepotMemorySource.ListedProject> rows)
    {
        if (_IsQualified(value, rows))
        {
            return null;
        }

        var parts = value.Split('/', 2);
        var matches = rows.Where(row => row.Address is not null && string.Equals(row.Slug, parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Address + value[parts[0].Length..] : null;
    }

    private static bool _IsQualified(string value, IReadOnlyList<DepotMemorySource.ListedProject> rows)
    {
        var parts = value.Split('/', 3);
        return parts.Length > 1 && rows.Any(row => string.Equals(row.Address, $"{parts[0]}/{parts[1]}", StringComparison.OrdinalIgnoreCase));
    }

    private async Task _LoadAsync(CancellationToken cancellationToken)
    {
        var result = await _host.CallMcpToolAsync(
            _connection.McpServerName, "list_projects", new Dictionary<string, object?> { ["includeSummary"] = false }, projectId: null, cancellationToken)
            .ConfigureAwait(false);

        if (result is { Outcome: PluginMcpToolCallOutcome.Success }
            && DepotMemorySource.TryParseProjects(result.Content ?? string.Empty, out var rows, out _))
        {
            await ObserveAsync(rows, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task _UpgradeStoredReferencesAsync(CancellationToken cancellationToken)
    {
        var rows = _rows;
        var left = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var upgraded = new List<string>();
        var count = await _host.RewriteProjectReferencesAsync(_scheme, value =>
        {
            var address = _Upgraded(value, rows);
            if (address is not null)
            {
                upgraded.Add($"{value} -> {address}");
            }
            else if (!_IsQualified(value, rows))
            {
                left.Add(value);
            }

            return address;
        }, cancellationToken).ConfigureAwait(false);

        var logger = _host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot");
        if (count > 0)
        {
            logger?.LogInformation("Depot connection '{Connection}': {Count} stored project reference(s) upgraded to organization/project: {References}", _connection.Name, count, string.Join(", ", upgraded));
        }

        if (left.Count > 0)
        {
            logger?.LogWarning("Depot connection '{Connection}': no single matching project for {References}; left as stored", _connection.Name, string.Join(", ", left));
            _host.ShowToast(
                $"Depot ({_connection.Name}): could not move {string.Join(", ", left)} to organization/project, since no single project matches. Pick the project again in its settings.",
                PluginToastSeverity.Warning);
        }
    }
}
