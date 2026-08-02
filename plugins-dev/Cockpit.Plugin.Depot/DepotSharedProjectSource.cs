using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// One Depot connection's own <see cref="ISharedProjectSource"/> (AC-245): lists this connection's projects through
/// <c>list_projects</c> (the same call <see cref="DepotMemorySource"/> already makes for its picker), then reads
/// each one's <c>.cockpit/project.json</c> (<see cref="CockpitProjectDefinitionStore.ReadAsync"/>, AC-244) to learn
/// its portable name and description — a Depot project without one is not offered here at all: not every project on
/// a connection has opted into being shared this way.
/// <para>
/// ponytail: one MCP round trip per listed project on top of the initial <c>list_projects</c> call, every time the
/// Projects workspace loads — no caching. Acceptable for the handful of projects a connection realistically carries
/// today; batch or cache here first if a connection with hundreds of shared projects makes this the slow part.
/// </para>
/// </summary>
internal sealed class DepotSharedProjectSource(DepotConnectionRegistration connection, string scheme, ICockpitHost host)
    : ISharedProjectSource
{
    public string Key => scheme;

    public string SourceName => $"Depot — {connection.Name}";

    public async Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken)
    {
        var listResult = await host.CallMcpToolAsync(
            connection.McpServerName,
            "list_projects",
            new Dictionary<string, object?> { ["includeSummary"] = false },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        switch (listResult.Outcome)
        {
            case PluginMcpToolCallOutcome.AuthorizationRequired:
                return SharedProjectListResult.Failed("Sign in to this Depot connection to see its shared projects.");
            case PluginMcpToolCallOutcome.Success:
                break;
            default:
                return SharedProjectListResult.Failed(
                    listResult.Error is { Length: > 0 } error ? error : "Depot did not return a list of projects.");
        }

        if (!DepotMemorySource.TryParseProjects(listResult.Content ?? string.Empty, out var listed, out var parseError))
        {
            return SharedProjectListResult.Failed(parseError ?? "Depot's project list came back in an unexpected shape.");
        }

        var shared = new List<SharedProject>(listed.Count);
        var unreadable = new List<UnreadableSharedProject>();
        foreach (var project in listed)
        {
            // DEP-159's list_projects mixes "Project" and "Brain" kinds together; a Brain is not something a
            // Cockpit project ever binds to.
            if (string.Equals(project.Kind, "Brain", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var role = DepotProjectRoleParser.Parse(project.Role);

            // Raymond, 2026-08-02: intended end state is Depot itself changing so a Viewer gets MCP read rights
            // (write stays Editor+) — a DEP-side fix, not a Cockpit one, and not shipped yet. Until it lands,
            // Depot's own access guard (measured against origin/dev: ReadFileQuery.cs requires ProjectRole.Editor)
            // makes every Viewer's read of .cockpit/project.json fail regardless of whether that project actually
            // carries one. The read is attempted for every role alike — once the DEP fix ships, a Viewer's read
            // simply starts succeeding here with no Cockpit-side change required — but a Viewer/Unknown-role
            // failure is reported as a named, visible-but-unreadable outcome instead of silently dropped, since
            // today it is ambiguous whether that project even opted into Cockpit sharing. An Editor/Owner failure
            // still means, unambiguously, "not shared this way" and is left out as before.
            var definitionResult = await CockpitProjectDefinitionStore.ReadAsync(
                host, connection.McpServerName, project.Slug, cancellationToken).ConfigureAwait(false);

            if (definitionResult.Outcome != PluginMcpToolCallOutcome.Success || definitionResult.Definition is not { } definition)
            {
                if (role is DepotProjectRole.Viewer or DepotProjectRole.Unknown)
                {
                    unreadable.Add(new UnreadableSharedProject(
                        $"{scheme}:{project.Slug}", project.Name is { Length: > 0 } ? project.Name : project.Slug, role.ToDisplayString()));
                }

                // Otherwise: no .cockpit/project.json at all (the ordinary case for a project that never opted into
                // Cockpit sharing) or a definition this build cannot parse — leave this one project out. One bad or
                // unshared project must never cost every other project on this connection.
                continue;
            }

            var name = definition.Name is { Length: > 0 }
                ? definition.Name
                : project.Name is { Length: > 0 } ? project.Name : project.Slug;

            shared.Add(new SharedProject($"{scheme}:{project.Slug}", name)
            {
                Description = definition.Description,
                Role = role.ToDisplayString(),
            });
        }

        return SharedProjectListResult.Success(shared) with { VisibleButUnreadable = unreadable };
    }

    /// <summary>
    /// Reads <c>.cockpit/project.json</c> a second time (AC-246), for the one project the operator is binding right
    /// now rather than every project on this connection — <see cref="ListAsync"/>'s own read only ever kept
    /// <see cref="CockpitProjectDefinition.Name"/>/<c>Description</c>, so a bind step needs its own call for the
    /// rest (<c>GitUrl</c>, <c>BehaviorPrompt</c>, the worktree switch, the MCP overlay, the resource rows).
    /// <see cref="id"/> is expected in this source's own shape (<c>"{scheme}:{slug}"</c>), so parsing it back is a
    /// prefix check against <paramref name="id"/>'s own scheme rather than a general <c>ProjectMemoryRef</c>-style
    /// parse — this plugin cannot reference <c>Cockpit.Core</c> (see this class's own remarks on
    /// <see cref="ProjectResourcePortabilityClassifier"/>), and it does not need to: it only ever has to recognise
    /// its own scheme, never anyone else's.
    /// </summary>
    public async Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken)
    {
        var prefix = $"{scheme}:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length)
        {
            return SharedProjectBindingResult.Failed($"'{id}' does not belong to this Depot connection.");
        }

        var slug = id[prefix.Length..];
        var definitionResult = await CockpitProjectDefinitionStore.ReadAsync(
            host, connection.McpServerName, slug, cancellationToken).ConfigureAwait(false);

        if (definitionResult.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return SharedProjectBindingResult.Failed("Sign in to this Depot connection to finish setting up this project.");
        }

        if (definitionResult.Outcome != PluginMcpToolCallOutcome.Success || definitionResult.Definition is not { } definition)
        {
            return SharedProjectBindingResult.Failed(
                definitionResult.Error is { Length: > 0 } error ? error : "Depot did not return a project definition.");
        }

        var name = definition.Name is { Length: > 0 } ? definition.Name : slug;

        return SharedProjectBindingResult.Success(new SharedProjectBinding(name)
        {
            Description = definition.Description,
            GitUrl = definition.GitUrl,
            BehaviorPrompt = definition.BehaviorPrompt,
            IsolateInWorktreeByDefault = definition.IsolateInWorktreeByDefault,
            EnabledMcpServerNames = definition.McpOverlay?.Enabled,
            // AC-246 (Raymond, 2026-08-02): a Placeholder row's Reference is blank on purpose — that is the row
            // saying "fill in your own path", not "nothing to name". Only a genuinely blank, non-placeholder
            // reference (malformed data — never something CockpitProjectResourceEntry.Create itself writes) is
            // left out here; SharedProjectBindingDialogViewModel is what turns a blank Reference into a question
            // row rather than a value to trust.
            Resources =
            [
                .. (definition.Resources ?? [])
                    .Where(resource => resource.Placeholder || !string.IsNullOrWhiteSpace(resource.Reference))
                    .Select(resource => new SharedProjectBindingResource(resource.Role, resource.Reference) { Label = resource.Label }),
            ],
        });
    }
}
