using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Assistant;

internal sealed partial class AssistantAgentGateway
{
    private async Task<(IReadOnlyList<string> Selection, IReadOnlySet<string>? PluginOwnedNames, string? Refusal)> _PluginMcpSelectionAsync(
        string pluginId, Project? project, SessionProfile profile, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        var names = new ExactMcpServerSelection(requested);
        if (_BrainServer(project, profile) is { } brain)
        {
            names.Add(brain);
        }
        var checks = await _PluginMcpChecksAsync(pluginId, project, profile, [.. names], cancellationToken).ConfigureAwait(false);
        var ownedNames = checks.Where(check => check.PinOwner).Select(check => check.Result.Name).ToHashSet(names.Comparer);
        return ([.. names], ownedNames.Count == 0 ? null : ownedNames, checks.FirstOrDefault(check => !check.Result.Ok).Result?.Error);
    }

    private async Task<IReadOnlyList<(PluginSessionMcpServerCheck Result, bool PinOwner)>> _PluginMcpChecksAsync(
        string pluginId, Project? project, SessionProfile profile, IReadOnlyList<string> requested, CancellationToken cancellationToken,
        IReadOnlySet<string>? delegationCeiling = null)
    {
        if (mcpServers is null)
        {
            return [.. requested.Select(name => (new PluginSessionMcpServerCheck(name, false, "The MCP catalog is unavailable."), false))];
        }

        var scoped = await mcpServers.GetServersForProjectAsync(project?.Id, cancellationToken).ConfigureAwait(false);
        var projectNames = McpServerRegistryFilter.OfferedToOperator(scoped)
            .Where(server => project?.McpOverlay.IsSelectedByDefault(server) == true)
            .Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitNames = (profile.EnabledMcpServerNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // AC-2394 (Raymond, 27-11): the profile's preselection as a session start reads it, so none means every server.
        var profileNames = McpServerRegistryFilter.ApplySessionSelection(scoped,
                McpServerRegistryFilter.EffectiveSessionSelection(null, profile.EnabledMcpServerNames))
            .Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var brain = _BrainServer(project, profile);
        var names = new ExactMcpServerSelection(requested);
        var available = await mcpServers.GetServersForSessionAsync(project?.Id, names, cancellationToken).ConfigureAwait(false);
        var policy = new McpSetPolicy(mcpServers, oauthTokens);
        List<(PluginSessionMcpServerCheck, bool)> checks = [];
        foreach (var name in requested)
        {
            var server = available.FirstOrDefault(server => names.Comparer.Equals(server.Name, name));
            var configured = projectNames.Contains(name) || profileNames.Contains(name) || names.Comparer.Equals(name, brain);
            var selected = projectNames.Contains(name) || explicitNames.Contains(name) || names.Comparer.Equals(name, brain);
            var pinOwner = server?.ContributingPluginId == pluginId && (!selected || server.Internal);
            var error = server is null ? $"MCP server '{name}' is unknown."
                : !configured && !pinOwner ? $"MCP server '{name}' is outside this plugin's session allowance."
                : await policy.RefusalAsync(available, [name], delegationCeiling: delegationCeiling,
                    cancellationToken: cancellationToken, owningPluginId: pluginId).ConfigureAwait(false);
            checks.Add((new(name, error is null, error), pinOwner));
        }

        return checks;
    }

    internal async Task<IReadOnlyList<PluginSessionMcpServerCheck>> CheckPluginMcpServersAsync(
        string pluginId, string? profileLabel, string? projectId, IReadOnlyList<string> serverNames, string? brain = null)
    {
        var (project, profile, error) = await _PluginMcpTargetAsync(profileLabel, projectId, CancellationToken.None).ConfigureAwait(false);
        if (profile is null)
        {
            return [.. serverNames.Select(name => new PluginSessionMcpServerCheck(name, false, error))];
        }
        if (project is null && sessionStartComposer is not null
            && await sessionStartComposer.ProjectIdForStartAsync(null, profile).ConfigureAwait(false) is { } inferredId)
        {
            project = await launcher.FindProjectByIdAsync(inferredId).ConfigureAwait(false);
        }
        var (brainProfile, brainRefusal) = _WithPluginBrain(project, profile, brain);
        if (brainRefusal is not null)
        {
            return [.. serverNames.Select(name => new PluginSessionMcpServerCheck(name, false, brainRefusal))];
        }
        var checks = await _PluginMcpChecksAsync(pluginId, project, brainProfile, serverNames, CancellationToken.None).ConfigureAwait(false);
        return [.. checks.Select(check => check.Result)];
    }

    private async Task<(Project? Project, SessionProfile? Profile, string? Error)> _PluginMcpTargetAsync(
        string? profileLabel, string? projectId, CancellationToken cancellationToken)
    {
        var project = string.IsNullOrEmpty(projectId) ? null : await launcher.FindProjectByIdAsync(projectId).ConfigureAwait(false);
        if (projectId is { Length: > 0 } && project is null)
        {
            return (null, null, "The project does not exist.");
        }
        var label = string.IsNullOrWhiteSpace(profileLabel) ? project?.DefaultProfileLabel : profileLabel;
        var profile = (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase));
        return profile is null ? (project, null, "The session profile does not exist or no profile was selected.") : (project, profile, null);
    }

    public Task<SessionMcpSetResult> GetSessionMcpSetAsync(string paneId, CancellationToken cancellationToken = default) =>
        _GetSessionMcpSetAsync(paneId, includeAvailable: true, cancellationToken);

    private async Task<SessionMcpSetResult> _GetSessionMcpSetAsync(string paneId, bool includeAvailable, CancellationToken cancellationToken)
    {
        if (NodeSessionAddress.Split(paneId) is not null || mcpMounts?.Set(paneId) is not { Context: { } context } set)
        {
            return new(false, null, [], "No local MCP set exists for that session.");
        }
        IReadOnlyList<McpServerConfig> registry = mcpServers is null ? [] : await mcpServers.GetServersForSessionAsync(
            context.Project?.Id, new ExactMcpServerSelection(set.ServerNames), cancellationToken).ConfigureAwait(false);
        var offered = !includeAvailable || mcpServers is null || context.Manager == SessionMcpManager.Assistant ? []
            : McpServerRegistryFilter.OfferedToOperator(await mcpServers.GetServersForSessionAsync(
                context.Project?.Id, null, cancellationToken).ConfigureAwait(false));
        var available = new List<SessionMcpServerState>();
        foreach (var server in offered.Where(server => !set.ServerNames.Contains(server.Name)))
        {
            var needsSignIn = server.Auth == McpServerAuth.OAuth && (oauthTokens is null ||
                await oauthTokens.GetAsync(server.IdentityKey, cancellationToken).ConfigureAwait(false) is null);
            available.Add(new(server.Name, needsSignIn ? "sign-in needed" : "available"));
        }
        var report = mcpMounts?.ConnectionReport(paneId);
        var brainServer = await _BrainServerOfAsync(paneId, context.Project, cancellationToken).ConfigureAwait(false);
        return new(true, context.Manager.ToString(), [.. set.ServerNames.Order(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var server = registry.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            var issue = report?.Issues.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            var status = issue is not null
                ? string.Equals(issue.Reason, "Needs a sign-in.", StringComparison.Ordinal) ? "sign-in needed" : "not reachable"
                : report?.ConnectedServerNames.Contains(name, StringComparer.OrdinalIgnoreCase) == true ? "connected"
                : server is null ? "unknown" : server.Enabled ? "selected" : "disabled";
            var isBrain = string.Equals(name, brainServer, StringComparison.OrdinalIgnoreCase);
            var reason = issue?.Reason ?? (server?.AlwaysMounted == true ? "Always mounted servers cannot be removed."
                : isBrain ? "This session's brain server; it is always mounted and cannot be removed." : null);
            return new SessionMcpServerState(name, status, reason, server?.AlwaysMounted != true && !isBrain);
        })], null) { AvailableServers = available };
    }

    private string? _BrainServer(Project? project, SessionProfile? profile) =>
        SessionStartDefaults.ResolveBrainMcpServer(project, profile, memorySources?.Sources.ToMemorySources());

    // AC-2394: a plugin's brain becomes the session's whole brain, only as a registered source serves it and only
    // where the project and profile name no other. The allowance then follows that brain, never a server name.
    private (SessionProfile Profile, string? Refusal) _WithPluginBrain(Project? project, SessionProfile profile, string? brain)
    {
        if (brain is null)
        {
            return (profile, null);
        }
        var sources = memorySources?.Sources.ToMemorySources();
        var registered = SessionStartDefaults.ResolveRegisteredBrain(brain, sources);
        var current = SessionStartDefaults.ResolveAssistant(project, profile);
        var refusal = registered is null ? $"The brain '{brain}' is not served by exactly one registered memory source."
            : current is not null && !string.Equals(SessionStartDefaults.ResolveRegisteredBrain(current, sources), registered, StringComparison.OrdinalIgnoreCase)
                ? $"The brain '{brain}' differs from the brain '{current}' that the project or profile names."
            : null;
        return (refusal is null ? profile with { Assistant = registered } : profile, refusal);
    }

    private async Task<string?> _BrainServerOfAsync(string paneId, Project? project, CancellationToken cancellationToken)
    {
        if (mcpMounts?.Set(paneId)?.Context?.Brain is { } brain)
        {
            return SessionStartDefaults.ResolveBrainMcpServer(brain, memorySources?.Sources.ToMemorySources());
        }
        var label = (paneId == AssistantIdentity.PaneId ? sessions.Assistant : sessions.Find(paneId))?.ActiveProfileLabel;
        var profile = label is null ? null : (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase));
        return _BrainServer(project, profile);
    }

    private const string PluginSessionMcpOwnershipError = "This plugin does not own that live session.";

    internal async Task<PluginSessionMcpResult> ReadPluginSessionMcpSetAsync(ISessionHandle? session)
    {
        if (session is null || !ReferenceEquals(sessions.Find(session.PaneId), session))
        {
            return new(false, [], false, PluginSessionMcpOwnershipError);
        }
        var result = await _GetSessionMcpSetAsync(session.PaneId, includeAvailable: false, CancellationToken.None).ConfigureAwait(false);
        return _PluginMcpResult(session, result);
    }

    internal async Task<PluginSessionMcpResult> AddPluginSessionMcpServerAsync(string pluginId, ISessionHandle? session, string name)
    {
        if (session is null)
        {
            return new(false, [], false, PluginSessionMcpOwnershipError);
        }
        var result = await _ChangeSessionMcpServerAsync(session.PaneId, name, add: true, byOperator: false,
            CancellationToken.None, pluginId, session).ConfigureAwait(false);
        if (result.Ok)
        {
            return _PluginMcpResult(session, result);
        }
        var current = await ReadPluginSessionMcpSetAsync(session).ConfigureAwait(false);
        return current with { Ok = false, Error = result.Error };
    }

    private PluginSessionMcpResult _PluginMcpResult(ISessionHandle session, SessionMcpSetResult result)
    {
        if (!ReferenceEquals(sessions.Find(session.PaneId), session))
        {
            return new(false, [], false, PluginSessionMcpOwnershipError);
        }
        var report = mcpMounts?.ConnectionReport(session.PaneId);
        var confirmed = report is { Settled: true } && result.Servers.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(report.ConnectedServerNames.Concat(report.Issues.Select(issue => issue.Name)));
        return new(result.Ok, [.. result.Servers.Select(row => new PluginSessionMcpServer(row.Name, row.Status))],
            result.Ok && confirmed, result.Error);
    }

    public async Task<SessionMcpSetResult> ChangeSessionMcpServerAsync(string paneId, string name, bool add,
        bool byOperator = false, CancellationToken cancellationToken = default)
    {
        // AC-2129: a controller or HoldsAssistant claim never substitutes for this machine's assistant identity.
        if (!byOperator && !string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return new(false, null, [], "Only the local assistant may manage servers on behalf of an agent.");
        }
        return await _ChangeSessionMcpServerAsync(paneId, name, add, byOperator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionMcpSetResult> _ChangeSessionMcpServerAsync(string paneId, string name, bool add, bool byOperator,
        CancellationToken cancellationToken, string? pluginId = null, ISessionHandle? ownedSession = null)
    {
        var session = paneId == AssistantIdentity.PaneId ? sessions.Assistant : sessions.Find(paneId);
        if (pluginId is not null && (session is null || !ReferenceEquals(session, ownedSession)))
        {
            return new(false, null, [], PluginSessionMcpOwnershipError);
        }
        var set = mcpMounts?.Set(paneId);
        string? pinOwner = null;
        var error = NodeSessionAddress.Split(paneId) is not null ? "MCP set management on nodes is outside v1."
            : set?.Context is null ? "No local MCP set exists for that session."
            : pluginId is not null ? null
            : byOperator && add && set.Context.Manager == SessionMcpManager.Assistant ? "The assistant manages this set. Ask it to add a server."
            : !add && string.Equals(name, await _BrainServerOfAsync(paneId, set.Context.Project, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)
                ? $"MCP server '{name}' is this session's brain server and cannot be removed."
            : await new McpSetPolicy(mcpServers, oauthTokens).RefusalAsync(set.Context.Project?.Id, [name], add,
                set.DelegationCeiling, cancellationToken).ConfigureAwait(false);
        if (error is null && pluginId is not null && session is not null && set?.Context is { } context)
        {
            var (project, profile, targetError) = await _PluginMcpTargetAsync(session.ActiveProfileLabel,
                context.Project?.Id ?? session.ProjectId, cancellationToken).ConfigureAwait(false);
            error = targetError;
            if (profile is not null)
            {
                profile = profile with { Assistant = context.Brain ?? profile.Assistant };
                var checks = await _PluginMcpChecksAsync(pluginId, project, profile, [name], cancellationToken, set.DelegationCeiling)
                    .ConfigureAwait(false);
                var check = checks[0];
                error = check.Result.Error;
                pinOwner = check.PinOwner ? pluginId : null;
            }
        }
        if (error is null && set is not null && set.ServerNames.Contains(name) == add && pinOwner is null)
        {
            return await _GetSessionMcpSetAsync(paneId, includeAvailable: pluginId is null, cancellationToken).ConfigureAwait(false);
        }
        var outsideProject = add && error is null
            ? await new McpSetPolicy(mcpServers, oauthTokens).OutsideProjectAsync(set?.Context?.Project?.Id, [name], cancellationToken).ConfigureAwait(false)
            : [];
        if (pluginId is not null && !ReferenceEquals(sessions.Find(paneId), ownedSession))
        {
            return new(false, null, [], PluginSessionMcpOwnershipError);
        }
        if (error is null && set is not null && mcpMounts is not null && !mcpMounts.TryChange(paneId, set, name, add, pinOwner))
        {
            error = "The MCP set changed concurrently. Read it again before retrying.";
        }
        await _RecordAsync(new AssistantSpawnAuditEntry(DateTimeOffset.Now,
            add ? AssistantSpawnAction.McpAdd : AssistantSpawnAction.McpRemove,
            pluginId is not null ? SpawnCaller.Plugin : byOperator ? SpawnCaller.Operator : SpawnCaller.Assistant,
            byOperator || pluginId is not null ? null : McpRequestContext.CurrentPaneId, session?.WorkspaceId ?? string.Empty, null,
            session?.ActiveProfileLabel, null, paneId, session?.Title, error, set?.Context?.Project?.Id, [name], outsideProject)
            { CallerPluginId = pluginId }, CancellationToken.None).ConfigureAwait(false);
        if (error is null)
        {
            var actor = pluginId ?? (byOperator ? "operator" : "assistant");
            var text = $"MCP {(add ? "added" : "removed")}: {name}; project {set?.Context?.Project?.Id ?? "none"}; by {actor}; outside project catalog: [{string.Join(", ", outsideProject)}]";
            logger?.LogInformation("Session {PaneId}: {McpChange}", paneId, text);
            inbox.Deliver("cockpit", AssistantIdentity.PaneId, "mcp-set-changed", text, subjectPaneId: paneId);
            if (session is not null)
            {
                try
                {
                    await session.UseControlAsync(control =>
                    {
                        control.RecordRow(new TranscriptSnapshotEntry(Guid.NewGuid().ToString("n"), "Divider", text,
                            null, null, null, null, false, DateTimeOffset.Now));
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "MCP set changed for {PaneId}, but its transcript divider could not be recorded", paneId);
                }
            }
        }
        return error is null ? await _GetSessionMcpSetAsync(paneId, includeAvailable: pluginId is null, cancellationToken).ConfigureAwait(false)
            : new(false, null, [], error);
    }
}
