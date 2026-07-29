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
/// A separate, host-free type — like <see cref="Cockpit.Plugin.YouTrack.YouTrackProjectField"/> is for a project
/// field — so a test can build registrations and assert on them without standing up an <c>ICockpitHost</c>.
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
            CheckReachability = host is null ? null : (value, cancellationToken) => _CheckReachabilityAsync(host, connection, value, cancellationToken),
        };

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
