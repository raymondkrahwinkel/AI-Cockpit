using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Assistant;

internal sealed partial class AssistantAgentGateway
{
    public async Task<SessionMcpSetResult> GetSessionMcpSetAsync(string paneId, CancellationToken cancellationToken = default)
    {
        if (NodeSessionAddress.Split(paneId) is not null || mcpMounts?.Set(paneId) is not { Context: { } context } set)
        {
            return new(false, null, [], "No local MCP set exists for that session.");
        }
        IReadOnlyList<McpServerConfig> registry = mcpServers is null ? [] : await mcpServers.GetServersForSessionAsync(
            context.Project?.Id, new ExactMcpServerSelection(set.ServerNames), cancellationToken).ConfigureAwait(false);
        var offered = mcpServers is null || context.Manager == SessionMcpManager.Assistant ? []
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
        return new(true, context.Manager.ToString(), [.. set.ServerNames.Order(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var server = registry.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            var issue = report?.Issues.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            var status = issue is not null
                ? string.Equals(issue.Reason, "Needs a sign-in.", StringComparison.Ordinal) ? "sign-in needed" : "not reachable"
                : report?.ConnectedServerNames.Contains(name, StringComparer.OrdinalIgnoreCase) == true ? "connected"
                : server is null ? "unknown" : server.Enabled ? "selected" : "disabled";
            var reason = issue?.Reason ?? (server?.AlwaysMounted == true ? "Always mounted servers cannot be removed." : null);
            return new SessionMcpServerState(name, status, reason, server?.AlwaysMounted != true);
        })], null) { AvailableServers = available };
    }

    public async Task<SessionMcpSetResult> ChangeSessionMcpServerAsync(string paneId, string name, bool add,
        bool byOperator = false, CancellationToken cancellationToken = default)
    {
        // AC-2129: a controller or HoldsAssistant claim never substitutes for this machine's assistant identity.
        if (!byOperator && !string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return new(false, null, [], "Only the local assistant may manage servers on behalf of an agent.");
        }
        var session = paneId == AssistantIdentity.PaneId ? sessions.Assistant : sessions.Find(paneId);
        var set = mcpMounts?.Set(paneId);
        var error = NodeSessionAddress.Split(paneId) is not null ? "MCP set management on nodes is outside v1."
            : set?.Context is null ? "No local MCP set exists for that session."
            : byOperator && add && set.Context.Manager == SessionMcpManager.Assistant ? "The assistant manages this set. Ask it to add a server."
            : await new McpSetPolicy(mcpServers, oauthTokens).RefusalAsync(set.Context.Project?.Id, [name], add,
                set.DelegationCeiling, cancellationToken).ConfigureAwait(false);
        if (error is null && set is not null && set.ServerNames.Contains(name) == add)
        {
            return await GetSessionMcpSetAsync(paneId, cancellationToken).ConfigureAwait(false);
        }
        var outsideProject = add && error is null
            ? await new McpSetPolicy(mcpServers, oauthTokens).OutsideProjectAsync(set?.Context?.Project?.Id, [name], cancellationToken).ConfigureAwait(false)
            : [];
        if (error is null && set is not null && mcpMounts is not null && !mcpMounts.TryChange(paneId, set, name, add))
        {
            error = "The MCP set changed concurrently. Read it again before retrying.";
        }
        await _RecordAsync(new AssistantSpawnAuditEntry(DateTimeOffset.Now,
            add ? AssistantSpawnAction.McpAdd : AssistantSpawnAction.McpRemove,
            byOperator ? SpawnCaller.Operator : SpawnCaller.Assistant,
            byOperator ? null : McpRequestContext.CurrentPaneId, session?.WorkspaceId ?? string.Empty, null,
            session?.ActiveProfileLabel, null, paneId, session?.Title, error, set?.Context?.Project?.Id, [name], outsideProject), CancellationToken.None).ConfigureAwait(false);
        if (error is null)
        {
            var text = $"MCP {(add ? "added" : "removed")}: {name}; project {set?.Context?.Project?.Id ?? "none"}; by {(byOperator ? "operator" : "assistant")}; outside project catalog: [{string.Join(", ", outsideProject)}]";
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
        return error is null ? await GetSessionMcpSetAsync(paneId, cancellationToken).ConfigureAwait(false) : new(false, null, [], error);
    }
}
