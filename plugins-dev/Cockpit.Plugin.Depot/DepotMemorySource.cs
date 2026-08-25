using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// Builds the memory-source registration(s) this plugin hands the host (AC-165/166, AC-501): one per configured
// Depot connection, so the picker can tell connections apart instead of a single fixed "Depot project". Kept
// separate from `DepotPlugin` so a test can assert on scheme/title/instruction without a real MCP server.
internal static class DepotMemorySource
{
    // The prefix a project's `MemoryRef` carries this source under — `depot:cockpit`. Never change it: an
    // already-linked project's stored reference is matched against it case-insensitively. Reserved for the
    // first connection in a settings list, so a project linked before AC-501 keeps resolving.
    public const string Scheme = "depot";

    // Paired with the connection it was built from so a caller can diff "before" against "after" by connection
    // identity (`Ui.DepotSettingsControl.Save`) rather than position. First connection keeps the plain `Scheme`;
    // later ones get a scheme namespaced from their own name, falling back to the stable `Id` on collision.
    public static IReadOnlyList<(DepotConnectionRegistration Connection, ProjectMemorySourceRegistration Registration)> BuildRegistrationPairs(
        IReadOnlyList<DepotConnectionRegistration> connections,
        ICockpitHost? host = null)
    {
        var schemesSoFar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(DepotConnectionRegistration, ProjectMemorySourceRegistration)>(connections.Count);

        for (var index = 0; index < connections.Count; index++)
        {
            var connection = connections[index];
            var scheme = index == 0 ? Scheme : _NamespacedScheme(connection, schemesSoFar);
            schemesSoFar.Add(scheme);
            pairs.Add((connection, _RegistrationFor(connection, scheme, host)));
        }

        return pairs;
    }

    // The registrations alone, in the same order — what `DepotPlugin.Initialize` hands the host at startup.
    public static IReadOnlyList<ProjectMemorySourceRegistration> BuildRegistrations(
        IReadOnlyList<DepotConnectionRegistration> connections, ICockpitHost? host = null) =>
        BuildRegistrationPairs(connections, host).Select(pair => pair.Registration).ToList();

    // One `ISharedProjectSource` per connection (AC-245), keyed under the same scheme as its memory source,
    // so `Ui.DepotSettingsControl` can sync both registrations by the same key when a connection changes.
    // `httpClient`: AC-763's blob PUT/GET, threaded through purely as a test seam.
    public static IReadOnlyList<ISharedProjectSource> BuildSharedProjectSources(
        IReadOnlyList<DepotConnectionRegistration> connections, ICockpitHost host, HttpClient? httpClient = null) =>
        BuildRegistrationPairs(connections, host)
            .Select(pair => (ISharedProjectSource)new DepotSharedProjectSource(pair.Connection, pair.Registration.Scheme, host, httpClient))
            .ToList();

    // AC-499: Title keeps naming the connection even though FamilyKey groups it under "Depot" in the picker
    // dropdown (InstanceTitle covers that). ProjectMemorySourceMapping still flattens to Scheme/Title/Instruction
    // for a session's standing instructions, so dropping the name here would make every connection read alike.
    private static ProjectMemorySourceRegistration _RegistrationFor(DepotConnectionRegistration connection, string scheme, ICockpitHost? host) =>
        new(
            scheme,
            $"Depot project — {connection.Name}",
            $"This project's memory lives in Depot instance \"{connection.Name}\". Read and write it through the "
                + "Depot MCP: look the project up by that slug before you start, and write back what you learn as "
                + "you go. If the Depot MCP is not available in this session, say so rather than working from "
                + "memory you cannot see.")
        {
            ListLocationsAsync = host is null ? null : cancellationToken => _ListLocationsAsync(connection, host, cancellationToken),
            SignInAsync = host is null ? null : async cancellationToken =>
                await host.SignInMcpServerAsync(connection.McpServerName, cancellationToken).ConfigureAwait(false) == PluginMcpSignInOutcome.Authorized,
            CheckReachability = host is null ? null : (value, cancellationToken) => _CheckReachabilityAsync(host, connection, value, cancellationToken),
            FamilyKey = Scheme,
            InstanceTitle = connection.Name,
        };

    // AC-502: lists this connection's Depot projects via `ICockpitHost.CallMcpToolAsync`'s `list_projects` —
    // the host owns the token. `includeSummary` is worth the extra server-side walk here (DEP-159): this is
    // a picker the operator opens once to make a choice, not a per-keystroke read.
    private static async Task<ProjectMemorySourceLocationsResult> _ListLocationsAsync(
        DepotConnectionRegistration connection, ICockpitHost host, CancellationToken cancellationToken)
    {
        var result = await host.CallMcpToolAsync(
            connection.McpServerName,
            "list_projects",
            new Dictionary<string, object?> { ["includeSummary"] = true },
            // A Depot connection is pushed into the shared MCP registry (AddMcpServer), not scoped to one project
            // (AC-500/AC-501), so it is reachable regardless of which project's editor opened this picker.
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PluginMcpToolCallOutcome.AuthorizationRequired:
                return ProjectMemorySourceLocationsResult.AuthorizationRequired;
            case PluginMcpToolCallOutcome.Success:
                return _ParseLocations(result.Content ?? string.Empty);
            default:
                return ProjectMemorySourceLocationsResult.Failed(
                    result.Error is { Length: > 0 } error ? error : "Depot did not return a list of projects.");
        }
    }

    private static ProjectMemorySourceLocationsResult _ParseLocations(string json)
    {
        if (!_TryParseProjects(json, out var projects, out var error))
        {
            return ProjectMemorySourceLocationsResult.Failed(error!);
        }

        var locations = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Slug))
            .Select(project => new ProjectMemorySourceLocation(
                project.Slug!,
                string.IsNullOrWhiteSpace(project.Name) ? project.Slug! : project.Name!,
                _DetailFor(project)))
            .ToList();

        return ProjectMemorySourceLocationsResult.Success(locations);
    }

    // Deserializes `list_projects`' own response shape, shared by the picker's own listing
    // (`_ParseLocations`) and the AC-499 reachability check (`_CheckReachabilityAsync`) —
    // one parse, two readers, rather than the same try/catch duplicated for each.
    private static bool _TryParseProjects(string json, out List<_ListProjectsProject> projects, out string? error)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<_ListProjectsPayload>(json, _SerializerOptions);
            if (payload?.Projects is not { } parsed)
            {
                projects = [];
                error = "Depot's project list came back in an unexpected shape.";
                return false;
            }

            projects = parsed;
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            projects = [];
            error = $"Couldn't read Depot's project list: {exception.Message}";
            return false;
        }
    }

    // AC-499: kind is shown first, then whatever else this project has to say — the same " · " separator the
    // document-count/updated pair below already uses, so a Brain among a picker full of Projects (Raymond's own
    // krahwinkel-it instance mixes both) reads apart at a glance rather than only being distinguishable by name.
    private static string? _DetailFor(_ListProjectsProject project)
    {
        var kind = project.Kind is { Length: > 0 } value ? value : null;
        var rest = _SummaryOrRoleFor(project);

        return (kind, rest) switch
        {
            (not null, not null) => $"{kind} · {rest}",
            (not null, null) => kind,
            (null, not null) => rest,
            (null, null) => null,
        };
    }

    private static string? _SummaryOrRoleFor(_ListProjectsProject project)
    {
        if (project.Summary is not { } summary)
        {
            return project.Role is { Length: > 0 } role ? role : null;
        }

        var documents = summary.DocumentCount == 1 ? "1 document" : $"{summary.DocumentCount} documents";
        return summary.LastModifiedAt is { } lastModified
            ? $"{documents} · updated {lastModified.ToLocalTime():d MMM yyyy}"
            : documents;
    }

    // AC-499: what a Confirmed reachability result shows — name and kind, free with the list_projects call the
    // match already needed. Deliberately not the document summary `_DetailFor` carries: this runs on every
    // debounced edit, so nothing here should need a server-side file-tree walk.
    private static string _ReachabilityDetailFor(_ListProjectsProject project)
    {
        var name = project.Name is { Length: > 0 } value ? value : project.Slug ?? string.Empty;
        return project.Kind is { Length: > 0 } kind ? $"{name} · {kind}" : name;
    }

    // AC-245: `list_projects`' own parsed rows (slug/name/role/kind), for `DepotSharedProjectSource`
    // — reuses `_TryParseProjects` rather than a second parser for the same response shape.
    internal static bool TryParseProjects(string json, out IReadOnlyList<ListedProject> projects, out string? error)
    {
        if (!_TryParseProjects(json, out var parsed, out error))
        {
            projects = [];
            return false;
        }

        projects = parsed
            .Where(project => !string.IsNullOrWhiteSpace(project.Slug))
            .Select(project => new ListedProject(project.Slug!, project.Name, project.Role, project.Kind))
            .ToList();
        return true;
    }

    // One `list_projects` row, the fields `DepotSharedProjectSource` needs — see `TryParseProjects`.
    internal readonly record struct ListedProject(string Slug, string? Name, string? Role, string? Kind);

    private static readonly JsonSerializerOptions _SerializerOptions = new(JsonSerializerDefaults.Web);

    // Mirrors only the fields DEP-159's list_projects response actually carries — {slug, name, role, kind} plus an
    // optional per-project summary — not a shared contract type, the same "plugin builds its own DTO for the tool
    // it calls" idiom the rest of this file already follows for DepotConnectionRegistration.
    private sealed class _ListProjectsPayload
    {
        public List<_ListProjectsProject>? Projects { get; set; }
    }

    private sealed class _ListProjectsProject
    {
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Role { get; set; }

        // AC-499: "Project" or "Brain" — Depot's list_projects returns both kinds mixed together for a caller whose
        // account has any, and nothing before this told the operator apart which was which in this plugin's own
        // picker or reachability confirmation.
        public string? Kind { get; set; }

        public _ListProjectsSummary? Summary { get; set; }
    }

    private sealed class _ListProjectsSummary
    {
        public int DocumentCount { get; set; }
        public DateTimeOffset? LastModifiedAt { get; set; }
    }

    // AC-503, rebuilt under AC-499: the original `outline` call failed (needs `path`) and mapped onto
    // NotSignedIn, showing signed-in operators a bogus "sign in" prompt. `list_projects` answers reliably
    // instead (`project_info` returns 200 with null fields for a nonexistent slug); `includeSummary: false` skips the walk.
    private static async Task<ProjectMemorySourceReachabilityResult> _CheckReachabilityAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string value, CancellationToken cancellationToken)
    {
        var result = await host.CallMcpToolAsync(
            connection.McpServerName,
            "list_projects",
            new Dictionary<string, object?> { ["includeSummary"] = false },
            // Same reasoning as _ListLocationsAsync: a Depot connection is shared, not project-scoped.
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PluginMcpToolCallOutcome.AuthorizationRequired:
                // The one case that actually means "go sign in" — see ProjectMemorySourceReachability's own remarks
                // on why this is no longer also where an ordinary failed call lands.
                return ProjectMemorySourceReachabilityResult.NotSignedIn;
            case PluginMcpToolCallOutcome.Success:
                return _MatchReachability(result.Content ?? string.Empty, value, host, connection);
            default:
                var reason = result.Error ?? "Depot did not return a list of projects.";
                _LogCheckFailure(host, connection, value, reason);
                return ProjectMemorySourceReachabilityResult.CheckFailed(result.Error);
        }
    }

    private static ProjectMemorySourceReachabilityResult _MatchReachability(
        string json, string value, ICockpitHost host, DepotConnectionRegistration connection)
    {
        if (!_TryParseProjects(json, out var projects, out var error))
        {
            _LogCheckFailure(host, connection, value, error!);
            return ProjectMemorySourceReachabilityResult.CheckFailed(error);
        }

        // Case-insensitive: a slug the operator typed with different casing than Depot stores it is still the same
        // project, not a reason to say "not found" for what a human would call an exact match.
        var match = projects.FirstOrDefault(project => string.Equals(project.Slug, value, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? ProjectMemorySourceReachabilityResult.NotFound
            : ProjectMemorySourceReachabilityResult.Confirmed(_ReachabilityDetailFor(match));
    }

    // AC-499: this trace didn't exist before, which is how a defect telling a signed-in operator to sign in
    // again went unnoticed. Resolved from `ICockpitHost.Services`; null on a test double is fine. Iron Law #8:
    // only the connection's non-secret name, the typed slug and the error text land here — never a token.
    private static void _LogCheckFailure(ICockpitHost host, DepotConnectionRegistration connection, string slug, string reason) =>
        host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot")
            .LogWarning("Depot reachability check for '{Slug}' against connection '{Connection}' failed: {Reason}", slug, connection.Name, reason);

    private static string _NamespacedScheme(DepotConnectionRegistration connection, ISet<string> taken)
    {
        var slugged = connection.Name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-');
        var slug = new string(slugged.ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-");
        }

        slug = slug.Trim('-');

        var candidate = slug.Length == 0 ? $"{Scheme}.{connection.Id}" : $"{Scheme}.{slug}";
        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        // The slug collided; its id breaks the tie. Ids are host-generated GUIDs so this rarely collides
        // either — the numbered fallback only matters for storage hand-edited into sharing an id.
        candidate = $"{Scheme}.{connection.Id}";
        var suffix = 2;
        while (taken.Contains(candidate))
        {
            candidate = $"{Scheme}.{connection.Id}-{suffix}";
            suffix++;
        }

        return candidate;
    }
}
