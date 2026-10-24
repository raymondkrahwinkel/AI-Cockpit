using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot;

internal enum DepotProjectListingOutcome
{
    Success,
    NotSignedIn,
    Forbidden,
    OlderDepot,
    Failed,
}

internal sealed record DepotProjectListingResult(
    DepotProjectListingOutcome Outcome,
    IReadOnlyList<DepotProjectListing> Projects,
    bool IncludesCockpit,
    string? Error = null,
    bool AuthenticatedOnly = false);

internal sealed record DepotProjectListing(
    string Slug,
    string? Address,
    string? Name,
    string? Role,
    string? Kind,
    DepotProjectOrganization? Organization,
    DepotProjectCockpit? Cockpit)
{
    public string Key => Address ?? Slug;
}

internal sealed record DepotProjectOrganization(string? Slug, string? Name, string? Role);

internal sealed record DepotProjectCockpit(
    string? State,
    string? Checksum,
    DateTimeOffset? UpdatedAt,
    CockpitProjectDefinition? Definition,
    bool? CanWrite = null);

// One project-list request and ETag cache per Depot connection. Plugin features share this source. An older Depot
// without the REST route gets no MCP fallback (AC-1642): the list fails and says the Depot is too old.
internal sealed class DepotProjectListingSource
{
    private static readonly ConditionalWeakTable<ICockpitHost, ConcurrentDictionary<string, DepotProjectListingSource>> _instances = new();
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly ICockpitHost _host;
    private readonly DepotConnectionRegistration _connection;
    private readonly HttpClient? _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal DepotProjectListingSource(ICockpitHost host, DepotConnectionRegistration connection, HttpClient? httpClient = null)
    {
        _host = host;
        _connection = connection;
        _httpClient = httpClient;
    }

    public static DepotProjectListingSource For(ICockpitHost host, DepotConnectionRegistration connection) =>
        _instances.GetOrCreateValue(host).GetOrAdd(
            $"{connection.Id}|{connection.McpServerName}|{connection.Url}", _ => new DepotProjectListingSource(host, connection));

    public async Task<DepotProjectListingResult> ListAsync(CancellationToken cancellationToken)
    {
        var endpoint = await _EndpointAsync(useStale: true, cancellationToken).ConfigureAwait(false);
        return endpoint.Outcome == DepotProjectListingOutcome.OlderDepot
            ? new(DepotProjectListingOutcome.Failed, [], false, "This Depot is too old for Cockpit: it has no project list over REST.")
            : endpoint;
    }

    // Reachability is an authentication probe, not a reason to mount a plugin-owned MCP tool. An older Depot proves
    // the same fact through /cockpit/me; a current one returns the project rows needed to validate the location too.
    public async Task<DepotProjectListingResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var projects = await _EndpointAsync(useStale: false, cancellationToken).ConfigureAwait(false);
        if (projects.Outcome != DepotProjectListingOutcome.OlderDepot)
        {
            return projects;
        }

        try
        {
            using var api = new DepotApi(_host, _connection, _httpClient);
            var response = await api.GetAsync("cockpit/me", cancellationToken).ConfigureAwait(false);
            return response.Outcome switch
            {
                DepotApiOutcome.Success => new(DepotProjectListingOutcome.Success, [], false, AuthenticatedOnly: true),
                DepotApiOutcome.NotSignedIn => new(DepotProjectListingOutcome.NotSignedIn, [], false),
                DepotApiOutcome.Forbidden => new(DepotProjectListingOutcome.Forbidden, [], false),
                _ => new(DepotProjectListingOutcome.Failed, [], false, "Depot could not be reached."),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(DepotProjectListingOutcome.Failed, [], false, exception.Message);
        }
    }

    private string CacheKey => $"project-list:{_connection.Id}:{_connection.Url}";

    private async Task<DepotProjectListingResult> _EndpointAsync(bool useStale, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = _host.Cache.Get<_CachedProjects>(CacheKey);
            try
            {
                using var api = new DepotApi(_host, _connection, _httpClient);
                var response = await api.GetAsync("cockpit/projects", cached?.Etag, cancellationToken).ConfigureAwait(false);
                if (response.Outcome == DepotApiOutcome.NotModified)
                {
                    return cached is { } known
                        ? _Success(known.Projects, known.IncludesCockpit)
                        : new(DepotProjectListingOutcome.Failed, [], false, "Depot confirmed an unchanged project list, but no local list is available.");
                }

                if (response.Outcome == DepotApiOutcome.NotSignedIn)
                {
                    return new(DepotProjectListingOutcome.NotSignedIn, [], false);
                }

                if (response.Outcome == DepotApiOutcome.Forbidden)
                {
                    return new(DepotProjectListingOutcome.Forbidden, [], false);
                }

                if (response.Outcome == DepotApiOutcome.OlderDepot)
                {
                    return new(DepotProjectListingOutcome.OlderDepot, [], false);
                }

                if (response.Outcome != DepotApiOutcome.Success)
                {
                    return _Unavailable(cached, useStale);
                }

                var projects = _Parse(response.Content.Span);
                _host.Cache.Set(CacheKey, new _CachedProjects(response.ETag, DateTimeOffset.UtcNow, true, projects));
                return _Success(projects, includesCockpit: true);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return _Unavailable(cached, useStale, exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<DepotProjectListing> _Parse(ReadOnlySpan<byte> json)
    {
        var payload = JsonSerializer.Deserialize<_ProjectsPayload>(json, _json);
        if (payload?.Projects is not { } projects)
        {
            throw new JsonException("Depot's project list came back in an unexpected shape.");
        }

        var listings = new List<DepotProjectListing>(projects.Count);
        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.Slug))
            {
                continue;
            }

            listings.Add(new(
                project.Slug,
                string.IsNullOrWhiteSpace(project.Address) ? null : project.Address,
                project.Name,
                project.Role,
                project.Kind,
                project.Organization is null ? null : new(project.Organization.Slug, project.Organization.Name, project.Organization.Role),
                project.Cockpit is null ? null : new(
                    project.Cockpit.State, project.Cockpit.Checksum, project.Cockpit.UpdatedAt,
                    project.Cockpit.Definition, project.Cockpit.CanWrite)));
        }

        return listings;
    }

    private static DepotProjectListingResult _Success(List<DepotProjectListing> projects, bool includesCockpit) =>
        new(DepotProjectListingOutcome.Success, projects, includesCockpit);

    private static DepotProjectListingResult _Unavailable(_CachedProjects? cached, bool useStale, string? error = null) =>
        useStale && cached is { } known
            ? new(
                DepotProjectListingOutcome.Success,
                known.Projects,
                known.IncludesCockpit,
                $"Depot is unavailable; last known list · {known.CachedAt.ToLocalTime():g}.")
            : new(DepotProjectListingOutcome.Failed, [], false, error is { Length: > 0 } value ? value : "Depot did not return a list of projects.");

    private sealed record _CachedProjects(string? Etag, DateTimeOffset CachedAt, bool IncludesCockpit, List<DepotProjectListing> Projects);

    private sealed class _ProjectsPayload
    {
        public List<_Project>? Projects { get; set; }
    }

    private sealed class _Project
    {
        public string? Address { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Role { get; set; }
        public string? Kind { get; set; }
        public _Organization? Organization { get; set; }
        public _Cockpit? Cockpit { get; set; }
    }

    private sealed class _Organization
    {
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Role { get; set; }
    }

    private sealed class _Cockpit
    {
        public string? State { get; set; }
        public string? Checksum { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public CockpitProjectDefinition? Definition { get; set; }
        public bool? CanWrite { get; set; }
    }
}
