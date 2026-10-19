using System.Net;
using Cockpit.Plugin.Depot.Backup;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.FirstStart;

// AC-1509: route C's backend half. The Depot being signed in to is offered to the host as `pending` (DepotPlugin's
// GetMcpServers) so the host's own sign-in can find it, and becomes a connection only once that sign-in succeeded.
// Before Initialize has run, keeping it is the storage write plus its backup target; Initialize registers the rest.
internal sealed class DepotFirstStart(
    ICockpitHost host,
    DepotSettings settings,
    Action<DepotConnectionRegistration?> setPending,
    Func<ICockpitHost?> initializedHost)
{
    public async Task<DepotFirstStartAnswer> SignInAsync(DepotFirstStartSignInRequest request, CancellationToken cancellationToken)
    {
        var url = DepotUrlNormalizer.Normalize(request.Url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return new(DepotFirstStartState.Invalid, "Enter the address of your Depot, such as https://depot.example.com.");
        }

        var before = settings.Connections;
        var existing = before.FirstOrDefault(connection => string.Equals(connection.Url, url, StringComparison.OrdinalIgnoreCase));
        var name = before.Any(connection => string.Equals(connection.Name, address.Host, StringComparison.OrdinalIgnoreCase))
            ? $"{address.Host} ({before.Count + 1})"
            : address.Host;
        var connection = existing ?? new DepotConnectionRegistration(Guid.NewGuid().ToString("n"), name, url);

        if (existing is null)
        {
            await _KeepAsync([.. before, connection], connection, cancellationToken).ConfigureAwait(false);
        }

        PluginMcpSignInOutcome outcome;
        setPending(existing is null ? connection : null);
        try
        {
            outcome = await host.SignInMcpServerAsync(connection.McpServerName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            setPending(null);
        }

        if (outcome != PluginMcpSignInOutcome.Authorized)
        {
            return outcome switch
            {
                PluginMcpSignInOutcome.Unreachable => new(DepotFirstStartState.Offline, $"Couldn't reach {url}. Check the address, or skip this and connect to Depot later from its settings."),
                PluginMcpSignInOutcome.Declined => new(DepotFirstStartState.NotSignedIn, "Sign-in didn't complete. Try again, or skip this and sign in later from the Depot settings."),
                _ => new(DepotFirstStartState.NotSignedIn, "Signing in isn't available on this Cockpit."),
            };
        }

        if (existing is null)
        {
            await _KeepAsync([.. before, connection], connection, cancellationToken).ConfigureAwait(false);
        }

        var notes = new List<string>();
        var target = new DepotBackupTarget(host, () => settings.Connections, connection.Id);
        var backups = await _BackupsAsync(target, notes, cancellationToken).ConfigureAwait(false);
        var manifest = await _ManifestAsync(connection, notes, cancellationToken).ConfigureAwait(false);
        return new(DepotFirstStartState.SignedIn, $"Signed in to {connection.Name}.", target.Id, backups, manifest, notes);
    }

    private async Task _KeepAsync(IReadOnlyList<DepotConnectionRegistration> after, DepotConnectionRegistration added, CancellationToken cancellationToken)
    {
        if (initializedHost() is { } initialized)
        {
            await DepotPlugin.WriteConnectionsAsync(initialized, settings, after, cancellationToken).ConfigureAwait(false);
            return;
        }

        settings.Connections = after;
        host.AddBackupTarget(new DepotBackupTarget(host, () => settings.Connections, added.Id));
    }

    // Newest first: C1 restores the latest one. A listing that fails hides C1 and says why, the sign-in still stands.
    private static async Task<IReadOnlyList<DepotFirstStartBackup>> _BackupsAsync(DepotBackupTarget target, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await target.ListAsync(cancellationToken).ConfigureAwait(false);
            return [.. entries.OrderByDescending(entry => entry.CreatedAt).Select(entry => new DepotFirstStartBackup(entry.Id, entry.CreatedAt, entry.Size))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            notes.Add($"Your backups could not be listed: {exception.Message}");
            return [];
        }
    }

    // DEP-266. A 404 means this Depot sets nothing up for an account, so C2 is simply not offered; that is no error.
    private async Task<string?> _ManifestAsync(DepotConnectionRegistration connection, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(host.CreateAuthorizedHandler(connection.McpServerName), disposeHandler: true);
            using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/manifest", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                notes.Add($"Your profile could not be read (HTTP {(int)response.StatusCode}).");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            notes.Add($"Your profile could not be read: {exception.Message}");
            return null;
        }
    }
}
