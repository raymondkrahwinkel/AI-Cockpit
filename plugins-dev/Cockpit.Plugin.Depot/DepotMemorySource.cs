using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// Builds the memory-source registration(s) this plugin hands the host (AC-165/166, AC-501): one per configured
/// Depot connection, so the project editor's picker can tell "Depot project — Wispslate" from "Depot project —
/// Synvolution" apart instead of the single fixed "Depot project" a pre-AC-501 install offered regardless of how
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
    /// own MCP server (AC-503) via <c>host.ProbeMcpToolAsync</c>. Null (the default, and what every existing test of
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
        try
        {
            var payload = JsonSerializer.Deserialize<_ListProjectsPayload>(json, _SerializerOptions);
            if (payload?.Projects is not { } projects)
            {
                return ProjectMemorySourceLocationsResult.Failed("Depot's project list came back in an unexpected shape.");
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
        catch (JsonException exception)
        {
            return ProjectMemorySourceLocationsResult.Failed($"Couldn't read Depot's project list: {exception.Message}");
        }
    }

    private static string? _DetailFor(_ListProjectsProject project)
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
        public _ListProjectsSummary? Summary { get; set; }
    }

    private sealed class _ListProjectsSummary
    {
        public int DocumentCount { get; set; }
        public DateTimeOffset? LastModifiedAt { get; set; }
    }

    /// <summary>
    /// The AC-503 reachability check for one Depot connection: confirms the slug the operator typed by asking this
    /// connection's own MCP server, through the host's out-of-session probe, rather than opening a session-owned
    /// connection this settings view has no business holding.
    /// <para>
    /// Tool name: <c>"outline"</c> — chosen without live access to a running Depot instance to verify it against
    /// (this plugin has no test fixture that stands one up), so this is a plausible guess, not a confirmed fact.
    /// <b>Verify the actual tool name/schema against a real Depot MCP server before this ships</b> — if it differs,
    /// every call here answers <see cref="McpProbeOutcome.Failed"/> (an unrecognised tool name is exactly the kind
    /// of ambiguous server error <see cref="McpProbeOutcome.Failed"/> exists for), which reads to the operator as
    /// "not signed in / unreachable" rather than the wrong tool being called — safe, per AC-503 acceptance criterion
    /// 4, but not informative until the name is confirmed. A single string argument named <c>"project"</c> carrying
    /// the typed slug is this method's other guess, for the same reason.
    /// </para>
    /// </summary>
    private const string ReachabilityToolName = "outline";

    private static async Task<ProjectMemorySourceReachabilityResult> _CheckReachabilityAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string value, CancellationToken cancellationToken)
    {
        var probe = await host.ProbeMcpToolAsync(
            connection.McpServerName,
            ReachabilityToolName,
            new Dictionary<string, object?> { ["project"] = value },
            cancellationToken).ConfigureAwait(false);

        // AC-503 acceptance criteria 3/4: NotSignedIn covers both "no sign-in" and "reaching it failed" (Failed) —
        // a transient network hiccup must never read as "this project does not exist", which would name the wrong
        // cause. NotFound is the one case the tool itself actually said so; DEP-136 (not yet built) is what would
        // let "does not exist" and "exists but this token cannot see it" be told apart, so both share this one
        // honest state until then — see ProjectMemorySourceReachability's own remarks.
        return probe.Outcome switch
        {
            McpProbeOutcome.Success => ProjectMemorySourceReachabilityResult.Confirmed(probe.Detail),
            McpProbeOutcome.NotFound => ProjectMemorySourceReachabilityResult.NotFound,
            _ => ProjectMemorySourceReachabilityResult.NotSignedIn,
        };
    }

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
