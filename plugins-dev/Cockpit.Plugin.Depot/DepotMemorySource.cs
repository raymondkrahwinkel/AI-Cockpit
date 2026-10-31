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
        ICockpitHost? host = null,
        HttpClient? httpClient = null)
    {
        var schemesSoFar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(DepotConnectionRegistration, ProjectMemorySourceRegistration)>(connections.Count);

        for (var index = 0; index < connections.Count; index++)
        {
            var connection = connections[index];
            var scheme = index == 0 ? Scheme : _NamespacedScheme(connection, schemesSoFar);
            schemesSoFar.Add(scheme);
            pairs.Add((connection, _RegistrationFor(connection, scheme, host, httpClient)));
        }

        return pairs;
    }

    // The registrations alone, in the same order — what `DepotPlugin.Initialize` hands the host at startup.
    public static IReadOnlyList<ProjectMemorySourceRegistration> BuildRegistrations(
        IReadOnlyList<DepotConnectionRegistration> connections, ICockpitHost? host = null, HttpClient? httpClient = null) =>
        BuildRegistrationPairs(connections, host, httpClient).Select(pair => pair.Registration).ToList();

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
    private static ProjectMemorySourceRegistration _RegistrationFor(
        DepotConnectionRegistration connection, string scheme, ICockpitHost? host, HttpClient? httpClient)
    {
        var title = $"Depot project — {connection.Name}";
        var instruction = $"This project's memory lives in Depot instance \"{connection.Name}\". Read and write it through the "
            + "Depot MCP: look the project up by that reference before you start, and write back what you learn as "
            + "you go. If the Depot MCP is not available in this session, say so rather than working from "
            + "memory you cannot see.";
        if (host is null)
        {
            return new(scheme, title, instruction)
            {
                FamilyKey = Scheme,
                InstanceTitle = connection.Name,
            };
        }

        var addressing = DepotProjectAddressing.For(host, connection, scheme);
        var projects = httpClient is null
            ? DepotProjectListingSource.For(host, connection)
            : new DepotProjectListingSource(host, connection, httpClient);
        return new(
            scheme,
            title,
            instruction)
        {
            ListLocationsAsync = cancellationToken => _ListLocationsAsync(projects, addressing, cancellationToken),
            SignInAsync = async cancellationToken =>
                await host.SignInMcpServerAsync(connection.McpServerName, cancellationToken).ConfigureAwait(false) == PluginMcpSignInOutcome.Authorized,
            CheckReachability = (value, cancellationToken) => _CheckReachabilityAsync(host, connection, projects, addressing, value, cancellationToken),
            AppendNoteAsync = (value, note, cancellationToken) => _AppendNoteAsync(host, connection, addressing, value, note, cancellationToken),
            ListBrainsAsync = cancellationToken => _ListBrainsAsync(connection, projects, cancellationToken),
            BrainInstruction = value => _BrainInstruction(connection, value),
            FamilyKey = Scheme,
            InstanceTitle = connection.Name,
        };
    }

    // AC-1992: a brain's value names its server as well (`<address>@<server>`), so a stored choice never loads a
    // same-named brain from another connection — not even once connections are reordered and their schemes move.
    private static string _BrainValue(DepotConnectionRegistration connection, DepotProjectListing brain) =>
        $"{brain.Key}@{_ServerOf(connection)}";

    private static string _ServerOf(DepotConnectionRegistration connection) =>
        Uri.TryCreate(connection.Url, UriKind.Absolute, out var uri) ? uri.Authority : connection.Url.Trim();

    // Brains only, from the same shared REST listing and ETag cache the location picker reads.
    private static async Task<ProjectMemorySourceLocationsResult> _ListBrainsAsync(
        DepotConnectionRegistration connection, DepotProjectListingSource projects, CancellationToken cancellationToken)
    {
        var result = await projects.ListAsync(cancellationToken).ConfigureAwait(false);
        if (_Unlisted(result) is { } unlisted)
        {
            return unlisted;
        }

        return ProjectMemorySourceLocationsResult.Success([.. result.Projects
            .Where(project => project.IsBrain)
            .Select(brain => new ProjectMemorySourceLocation(
                _BrainValue(connection, brain),
                string.IsNullOrWhiteSpace(brain.Name) ? brain.Slug : brain.Name,
                $"{brain.Key} · {connection.Name}"))]);
    }

    // Null for a value of another server: that brain is not this connection's to load, whatever its scheme says.
    private static string? _BrainInstruction(DepotConnectionRegistration connection, string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || !string.Equals(value[(at + 1)..], _ServerOf(connection), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Load it through the \"{connection.McpServerName}\" MCP server (Depot instance \"{connection.Name}\" at "
            + $"{connection.Url}): call its bootstrap tool for the brain \"{value[..at]}\" and follow the instructions it "
            + "returns. If that MCP server is not available in this session, say so rather than working without the brain.";
    }

    // The answer for a listing that produced no rows to offer, or null when it did.
    private static ProjectMemorySourceLocationsResult? _Unlisted(DepotProjectListingResult result) => result.Outcome switch
    {
        DepotProjectListingOutcome.Success => null,
        DepotProjectListingOutcome.NotSignedIn => ProjectMemorySourceLocationsResult.AuthorizationRequired,
        _ => ProjectMemorySourceLocationsResult.Failed(result.Error ?? "Depot did not return a list of projects."),
    };

    // The picker uses the connection's shared REST listing and ETag cache.
    private static async Task<ProjectMemorySourceLocationsResult> _ListLocationsAsync(
        DepotProjectListingSource projects, DepotProjectAddressing addressing, CancellationToken cancellationToken)
    {
        var result = await projects.ListAsync(cancellationToken).ConfigureAwait(false);
        if (_Unlisted(result) is { } unlisted)
        {
            return unlisted;
        }

        await addressing.ObserveAsync(result.Projects, cancellationToken).ConfigureAwait(false);

        var locations = result.Projects
            .Select(project => new ProjectMemorySourceLocation(
                project.Key,
                string.IsNullOrWhiteSpace(project.Name) ? project.Slug : project.Name,
                _DetailFor(project)))
            .ToList();

        return ProjectMemorySourceLocationsResult.Success(locations);
    }

    // Kind is shown first, then the caller's role, so a Brain among Projects reads apart at a glance.
    private static string? _DetailFor(DepotProjectListing project)
    {
        var kind = project.Kind is { Length: > 0 } value ? value : null;
        var rest = project.Role is { Length: > 0 } role ? role : null;

        return (kind, rest) switch
        {
            (not null, not null) => $"{kind} · {rest}",
            (not null, null) => kind,
            (null, not null) => rest,
            (null, null) => null,
        };
    }

    // What a confirmed reachability result shows, free with the REST listing already needed for the match.
    private static string _ReachabilityDetailFor(DepotProjectListing project)
    {
        var name = project.Name is { Length: > 0 } value ? value : project.Slug;
        return project.Kind is { Length: > 0 } kind ? $"{name} · {kind}" : name;
    }

    // A current Depot proves reachability with its project list; an older one uses /cockpit/me as the auth probe.
    private static async Task<ProjectMemorySourceReachabilityResult> _CheckReachabilityAsync(
        ICockpitHost host, DepotConnectionRegistration connection, DepotProjectListingSource projects,
        DepotProjectAddressing addressing, string value, CancellationToken cancellationToken)
    {
        var result = await projects.ProbeAsync(cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case DepotProjectListingOutcome.NotSignedIn:
                // The one case that actually means "go sign in" — see ProjectMemorySourceReachability's own remarks
                // on why this is no longer also where an ordinary failed call lands.
                return ProjectMemorySourceReachabilityResult.NotSignedIn;
            case DepotProjectListingOutcome.Success when result.AuthenticatedOnly:
                return ProjectMemorySourceReachabilityResult.Confirmed(value);
            case DepotProjectListingOutcome.Success:
                return await _MatchReachabilityAsync(result.Projects, value, addressing, cancellationToken).ConfigureAwait(false);
            default:
                var reason = result.Error ?? "Depot did not return a list of projects.";
                _LogCheckFailure(host, connection, value, reason);
                return ProjectMemorySourceReachabilityResult.CheckFailed(reason);
        }
    }

    // AC-492: where the host's quick notes land inside a Depot project. A fixed name at the root so a session that
    // follows this source's Instruction finds them without being told; the file is created on first append.
    internal const string NotesPath = "Inbox.md";

    // AC-492: `append` is Depot's server-side atomic append — safe under concurrent writers, and the only Depot
    // tool this writer may call: `write` would replace the file, which the contract forbids.
    private static async Task<ProjectMemoryAppendResult> _AppendNoteAsync(
        ICockpitHost host, DepotConnectionRegistration connection, DepotProjectAddressing addressing, string value, string note, CancellationToken cancellationToken)
    {
        var projectAddress = await addressing.ProjectArgumentAsync(value, cancellationToken).ConfigureAwait(false);
        var address = projectAddress.Split('/', 2);
        if (address.Length != 2 || address.Any(string.IsNullOrWhiteSpace))
        {
            return ProjectMemoryAppendResult.Failed(
                $"Depot project '{projectAddress}' has no organization-qualified address.");
        }

        DepotApiResult result;
        try
        {
            using var api = new DepotApi(host, connection);
            var path = $"sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/append";
            result = await api.PostJsonAsync(path, new { path = NotesPath, content = note }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ProjectMemoryAppendResult.Failed(exception.Message);
        }

        return result.Outcome switch
        {
            DepotApiOutcome.Success => ProjectMemoryAppendResult.Success,
            DepotApiOutcome.NotSignedIn => ProjectMemoryAppendResult.AuthorizationRequired,
            _ => ProjectMemoryAppendResult.Failed("Depot did not accept the note."),
        };
    }

    private static async Task<ProjectMemorySourceReachabilityResult> _MatchReachabilityAsync(
        IReadOnlyList<DepotProjectListing> projects, string value, DepotProjectAddressing addressing, CancellationToken cancellationToken)
    {
        await addressing.ObserveAsync(projects, cancellationToken).ConfigureAwait(false);

        // Case-insensitive; an org/slug value needs the address, a bare one the slug, and a slug two organizations share matches nothing.
        var matches = projects.Where(project => _Matches(project, value)).ToList();
        return matches.Count == 1
            ? ProjectMemorySourceReachabilityResult.Confirmed(_ReachabilityDetailFor(matches[0]))
            : ProjectMemorySourceReachabilityResult.NotFound;
    }

    private static bool _Matches(DepotProjectListing project, string value) =>
        string.IsNullOrWhiteSpace(project.Address)
            ? string.Equals(project.Slug, value[(value.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(project.Address, value, StringComparison.OrdinalIgnoreCase)
                || (!value.Contains('/') && string.Equals(project.Slug, value, StringComparison.OrdinalIgnoreCase));

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
