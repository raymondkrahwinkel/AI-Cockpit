using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Backup;

namespace Cockpit.Plugin.Depot.Backup;

// AC-1508: a connection's account space in Depot (DEP-168, /cockpit/backups) as a backup target. The host's own bearer
// goes on every request through CreateAuthorizedHandler, so this plugin never holds a token. Depot keeps the newest 10
// and caps each one (10 MiB by default); both are its own rules, and a refusal comes back with Depot's reason.
internal sealed class DepotBackupTarget(
    ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    string connectionId,
    Func<HttpMessageHandler>? handler = null) : IBackupTarget
{
    public static string IdFor(string connectionId) => $"depot:{connectionId}";

    public string Id => IdFor(connectionId);

    public string DisplayName => _Connection() is { } connection ? $"Depot: {connection.Name}" : "Depot (connection removed)";

    public async Task<string> WriteAsync(string name, Stream archive, CancellationToken cancellationToken = default)
    {
        var connection = _Required();

        // Copied, never wrapped: StreamContent would dispose the host's stream, which it reads again afterwards.
        // Depot reads a backup whole up to its cap anyway.
        using var copy = new MemoryStream();
        await archive.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        var bytes = copy.ToArray();
        var length = bytes.LongLength;
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var http = _Client(connection);
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.PostAsync(_Backups(connection), body, cancellationToken).ConfigureAwait(false);
        await _EnsureAsync(response, connection, cancellationToken).ConfigureAwait(false);

        var stored = await response.Content.ReadFromJsonAsync<_Backup>(cancellationToken).ConfigureAwait(false);

        // What Depot says it stored must be what was sent; otherwise it is removed again, never listed as a backup.
        if (stored is null)
        {
            if (stored is not null)
            {
                await DeleteAsync(stored.Id.ToString(), CancellationToken.None).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"The backup did not arrive whole in {connection.Name}, so nothing was kept there. Try again.");
        }

        return stored.Id.ToString();
    }

    public async Task<IReadOnlyList<BackupTargetEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var connection = _Required();
        using var http = _Client(connection);
        using var response = await http.GetAsync(_Backups(connection), cancellationToken).ConfigureAwait(false);
        await _EnsureAsync(response, connection, cancellationToken).ConfigureAwait(false);

        var backups = await response.Content.ReadFromJsonAsync<List<_Backup>>(cancellationToken).ConfigureAwait(false) ?? [];
        return [.. backups.Select(backup => new BackupTargetEntry(backup.Id.ToString(), new DateTimeOffset(DateTime.SpecifyKind(backup.CreatedAt, DateTimeKind.Utc)), backup.Size, null))];
    }

    // Read whole: the response and its client go when this returns, and Depot's cap keeps a backup small.
    public async Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = _Required();
        using var http = _Client(connection);
        using var response = await http.GetAsync($"{_Backups(connection)}/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"That backup is no longer in {connection.Name}.");
        }

        await _EnsureAsync(response, connection, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), writable: false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = _Required();
        using var http = _Client(connection);
        using var response = await http.DeleteAsync($"{_Backups(connection)}/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await _EnsureAsync(response, connection, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private sealed record _Backup(Guid Id, long Size, string Sha256, DateTime CreatedAt);

    private DepotConnectionRegistration? _Connection() => connections().FirstOrDefault(connection => connection.Id == connectionId);

    private DepotConnectionRegistration _Required() =>
        _Connection() ?? throw new InvalidOperationException("That Depot connection is no longer configured.");

    private HttpClient _Client(DepotConnectionRegistration connection) =>
        new(handler?.Invoke() ?? host.CreateAuthorizedHandler(connection.McpServerName), disposeHandler: true);

    private static string _Backups(DepotConnectionRegistration connection) => $"{connection.Url.TrimEnd('/')}/cockpit/backups";

    // Depot's own words where it gave them: a too-large backup says how large it may be.
    private static async Task _EnsureAsync(HttpResponseMessage response, DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"sign-in needed. Sign in to {connection.Name} in the Depot settings first.",
            HttpStatusCode.RequestEntityTooLarge => $"{connection.Name} refused the backup: {await _ReasonAsync(response, cancellationToken).ConfigureAwait(false)}",
            _ => $"{connection.Name} refused the request (HTTP {(int)response.StatusCode}).",
        });
    }

    private static async Task<string> _ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<_Problem>(cancellationToken).ConfigureAwait(false);
            return problem?.Detail is { Length: > 0 } detail ? detail : "it is larger than this Depot accepts.";
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            return "it is larger than this Depot accepts.";
        }
    }

    private sealed record _Problem(string? Detail);
}
