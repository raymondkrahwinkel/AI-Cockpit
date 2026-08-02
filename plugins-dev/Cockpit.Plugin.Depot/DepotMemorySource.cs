using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// Builds the memory-source registration(s) this plugin hands the host (AC-165/166, AC-501): one per configured
/// Depot connection, so the project editor's picker can tell "Depot project — Wispslate" from "Depot project —
/// Acme" apart instead of the single fixed "Depot project" a pre-AC-501 install offered regardless of how
/// many instances were connected.
/// <para>
/// A separate type from <see cref="DepotPlugin"/> and <see cref="Ui.DepotSettingsControl"/> — like
/// <see cref="Cockpit.Plugin.YouTrack.YouTrackProjectField"/> is for a project field — so a test can build
/// registrations and assert on the scheme/title/instruction shape without a real MCP server behind them. Since
/// AC-502 it does take an <c>ICockpitHost</c>: each registration's <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/>
/// closes over it to call this connection's own contributed server, but that delegate is never invoked at
/// registration-build time — a test still gets a real, comparable registration without ever calling it.
/// </para>
/// </summary>
internal static class DepotMemorySource
{
    /// <summary>
    /// The prefix a project's <c>MemoryRef</c> carries this source under — <c>depot:cockpit</c>. Never change it:
    /// an already-linked project's stored reference is matched against it case-insensitively. Reserved for the
    /// first connection in a settings list (see <see cref="BuildRegistrationPairs"/>), so a project linked before
    /// AC-501 ever existed keeps resolving, whichever connection now happens to be first.
    /// </summary>
    public const string Scheme = "depot";

    /// <summary>
    /// One registration per connection, in the same order <paramref name="connections"/> lists them, paired with
    /// the connection it was built from — the pairing is what a caller needs to diff "before" against "after" by
    /// connection identity when a save changes the list (<see cref="Ui.DepotSettingsControl.Save"/>), rather than by
    /// position, which a reorder or a removal ahead of a connection would shift out from under it.
    /// <para>
    /// The first connection keeps the plain <see cref="Scheme"/>; every later one gets a scheme namespaced from its
    /// own name (<c>depot.wispslate</c>), falling back to its stable <see cref="DepotConnectionRegistration.Id"/>
    /// when the name yields nothing usable (an all-symbol name, e.g.) or collides with another connection's own slug
    /// — a GUID-derived id is always a scheme <see cref="ProjectMemoryRef.IsUsableScheme"/> accepts and is unique by
    /// construction, so a connection is never silently dropped for want of a nameable scheme.
    /// </para>
    /// </summary>
    /// <param name="connections">The configured connections.</param>
    /// <param name="host">
    /// Wires each registration's <see cref="ProjectMemorySourceRegistration.CheckReachability"/> to this connection's
    /// own MCP server (AC-503, rebuilt AC-499) via <c>host.CallMcpToolAsync</c>'s <c>list_projects</c> — see
    /// <see cref="_CheckReachabilityAsync"/>'s own remarks. Null (the default, and what every existing test of
    /// this method already passes) leaves <c>CheckReachability</c> unset — a row behaves exactly as it does today,
    /// nothing shown under it. Optional rather than a second required parameter so this stays the same host-free type
    /// the class remarks describe: a test can still build registrations and assert on them without standing up an
    /// <c>ICockpitHost</c>.
    /// </param>
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

    /// <summary>The registrations alone, in the same order — what <see cref="DepotPlugin.Initialize"/> hands the host at startup.</summary>
    public static IReadOnlyList<ProjectMemorySourceRegistration> BuildRegistrations(
        IReadOnlyList<DepotConnectionRegistration> connections, ICockpitHost? host = null) =>
        BuildRegistrationPairs(connections, host).Select(pair => pair.Registration).ToList();

    /// <summary>
    /// One <see cref="ISharedProjectSource"/> per connection (AC-245), keyed under the same scheme
    /// <see cref="BuildRegistrationPairs"/> would give that connection's own memory source — so a
    /// <see cref="SharedProject.Id"/> this returns is exactly the <c>MemoryRef</c> a project would carry once bound
    /// to it, and <see cref="Ui.DepotSettingsControl"/> can sync both registrations by the same key when a
    /// connection is added, renamed or removed.
    /// </summary>
    public static IReadOnlyList<ISharedProjectSource> BuildSharedProjectSources(
        IReadOnlyList<DepotConnectionRegistration> connections, ICockpitHost host) =>
        BuildRegistrationPairs(connections, host)
            .Select(pair => (ISharedProjectSource)new DepotSharedProjectSource(pair.Connection, pair.Registration.Scheme, host))
            .ToList();

    // AC-499: Title keeps naming the connection ("Depot project — Wispslate") even though the picker's own dropdown
    // no longer shows it once FamilyKey groups this registration under "Depot" — the instance dropdown reads
    // InstanceTitle instead (ProjectDialogViewModel.CreateAsync). Title still has a reader that never sees
    // FamilyKey/InstanceTitle at all: ProjectMemorySourceMapping flattens a registration to Scheme/Title/Instruction
    // for a session's own standing instructions, so Title is the only surviving name once a session is told where
    // its memory lives — dropping the connection name from it would make that sentence read "your memory lives in
    // 'Depot project'" for every connection alike.
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

    /// <summary>
    /// AC-502: lists this connection's own Depot projects through its contributed MCP server's <c>list_projects</c>
    /// tool, via <see cref="ICockpitHost.CallMcpToolAsync"/> — the host owns the token, this plugin only ever sees
    /// the tool's JSON text result. <c>includeSummary</c> is worth the extra server-side walk here (DEP-159): this
    /// is a picker the operator opens once to make a choice, not a per-keystroke read.
    /// </summary>
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

    /// <summary>
    /// Deserializes <c>list_projects</c>' own response shape, shared by the picker's own listing
    /// (<see cref="_ParseLocations"/>) and the AC-499 reachability check (<see cref="_CheckReachabilityAsync"/>) —
    /// one parse, two readers, rather than the same try/catch duplicated for each.
    /// </summary>
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

    // AC-499: what a Confirmed reachability result shows under the row — the project's own name and kind, since
    // that data comes free with the same list_projects call the match itself needed. Deliberately not the document
    // summary _DetailFor also carries: this call runs on every debounced edit (see its own remarks on
    // includeSummary: false), so nothing here should invite a plugin to wish it had asked the server to walk a
    // project's file tree just to fill this line in.
    private static string _ReachabilityDetailFor(_ListProjectsProject project)
    {
        var name = project.Name is { Length: > 0 } value ? value : project.Slug ?? string.Empty;
        return project.Kind is { Length: > 0 } kind ? $"{name} · {kind}" : name;
    }

    /// <summary>
    /// AC-245: <c>list_projects</c>' own parsed rows (slug/name/role/kind), for <see cref="DepotSharedProjectSource"/>
    /// — reuses <see cref="_TryParseProjects"/> rather than a second parser for the same response shape.
    /// </summary>
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

    /// <summary>One <c>list_projects</c> row, the fields <see cref="DepotSharedProjectSource"/> needs — see <see cref="TryParseProjects"/>.</summary>
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

    /// <summary>
    /// The AC-503 reachability check for one Depot connection, rebuilt under AC-499 after being measured against a
    /// real Depot server: the original guess — a tool named <c>"outline"</c> called with only <c>{"project": value}"</c>
    /// — was wrong. Depot's own <c>outline</c> is a single-<em>document</em> tool (<c>required: ["project", "path"]</c>);
    /// called without <c>path</c> it always failed, and that failure mapped onto <see cref="ProjectMemorySourceReachability.NotSignedIn"/>,
    /// so a fully signed-in operator kept reading "sign in to confirm this location" for a check that could never
    /// have succeeded regardless of sign-in state.
    /// <para>
    /// This calls <c>list_projects</c> instead — the same tool <see cref="_ListLocationsAsync"/> already uses for
    /// the picker, and the one Depot tool that actually answers "does this slug exist and can this operator see it":
    /// <c>project_info(project)</c> was measured as unusable for this (a nonexistent slug answers 200 with every
    /// field null rather than an error), so a slug lookup against the operator's own visible project list is the
    /// only reliable "exists and I can see it" this server offers. <c>includeSummary: false</c> — unlike the
    /// picker's own call — because Depot's own tool description warns that flag "walks each returned project's file
    /// tree server-side"; the picker is opened once, but this check reruns on every debounced edit, and nothing it
    /// needs (only slug/name/kind) requires that walk.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// AC-499: the trace this check left nowhere before — grepping the dev log for this probe found nothing, which
    /// is exactly how a defect that silently told a signed-in operator to sign in again stayed unnoticed. Resolved
    /// from <see cref="ICockpitHost.Services"/>, the same shared container <c>Cockpit.App.Plugins.CockpitHost</c>'s
    /// own internal logging already resolves <c>ILoggerFactory</c> from — no new host member, just the DI seam every
    /// plugin already has. Null on a host/test double with no logging registered (most tests): a missing log line is
    /// not a failure this check itself needs to report on. Iron Law #8: only the connection's own (non-secret) name,
    /// the typed slug and the plugin's own error text ever land here — never a token, never anything this call used
    /// to authenticate.
    /// </summary>
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

        // The slug collided with another connection's; its own id breaks that tie. Ids are host-generated GUIDs, so
        // in practice this never collides either — the numbered fallback below only matters for storage that was
        // hand-edited or corrupted into sharing an id, and guarantees BuildRegistrationPairs still never hands back
        // two connections the same scheme, whatever the input.
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
