using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Mcp;

// AC-1386: what a node caller may see and start, shared by the node tools and the backend API so the two cannot
// drift. Seeing is reaching: the set that lists a session is the set that may read, prompt, stop or answer it.
internal sealed class NodeCallerSessionPolicy(
    IAssistantReadGateway read,
    IAssistantAgentGateway gateway,
    INodePairingBroker pairing,
    ISessionProfileStore profiles,
    IProfileLoginChecker? logins)
{
    public const string PersonalLoginRefusal = "This profile uses a personal subscription and can only be started by its owner. Use an API-key profile for shared use. If this key is yours, mark it as your own device under this server's Connect keys in Options.";

    // Live against the grant: an unticked profile disappears from the list immediately, and the node operator's own
    // sessions are visible under a shared profile.
    // AC-2003: a key not issued to the owner neither sees nor drives a session on the owner's subscription.
    public async Task<IReadOnlyList<AssistantSessionRow>> VisibleSessionsAsync(NodeCaller caller)
    {
        var sessions = await read.ListSessionsAsync().ConfigureAwait(false);
        var reachable = await ReachableProfilesAsync(caller, profiles, logins).ConfigureAwait(false);
        return [.. sessions.Where(session => caller.AllowsSession(session.Profile, session.ProjectId, pairing) && reachable(session.Profile))];
    }

    // Read once per call, so a stream holds the profiles as they were when it opened; an unknown label, or no store, is
    // not reachable.
    public static async Task<Func<string, bool>> ReachableProfilesAsync(NodeCaller caller, ISessionProfileStore? profiles, IProfileLoginChecker? logins)
    {
        if (caller.IsOwner)
        {
            return _ => true;
        }

        var known = profiles is null ? [] : await profiles.LoadAsync().ConfigureAwait(false);
        var shareable = known.Where(profile => !UsesPersonalLogin(profile, logins)).Select(profile => profile.Label);
        var labels = new HashSet<string>(shareable, StringComparer.OrdinalIgnoreCase);
        return labels.Contains;
    }

    public async Task<bool> IsVisibleAsync(NodeCaller caller, string paneId)
    {
        var visible = await VisibleSessionsAsync(caller).ConfigureAwait(false);
        return visible.Any(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));
    }

    // Both checks against the live grant, not a copy read at pairing time, so unticking a row takes effect on the
    // next call. The label is resolved to a real profile first, compared the way the spawn path compares
    // (`AssistantAgentGateway`: OrdinalIgnoreCase), so "Foo"/"foo" can't pass a grant check on the wrong one.
    public async Task<NodeStartCheck> CheckStartAsync(NodeCaller caller, string profile, string? projectId)
    {
        var known = await profiles.LoadAsync().ConfigureAwait(false);
        var allowedProfile = known.FirstOrDefault(candidate => string.Equals(candidate.Label, profile.Trim(), StringComparison.OrdinalIgnoreCase));
        if (allowedProfile is null || !caller.AllowsProfile(allowedProfile.Label, pairing))
        {
            return new NodeStartCheck(null, caller.ByConnectKey
                ? $"The scope of this connect key does not include the profile '{profile}'. Call list_node_profiles for the ones it does."
                : $"This node's operator has not allowed the profile '{profile}'. Call list_node_profiles for the ones they have, and ask them to tick it on that machine if the one you want is missing.");
        }

        // AC-2003: a hard block, not a scope a grant can widen — a subscription is for its owner alone.
        if (!caller.IsOwner && UsesPersonalLogin(allowedProfile, logins))
        {
            return new NodeStartCheck(null, PersonalLoginRefusal);
        }

        // AC-1367: in scope is not enough for a profile that skips its approvals — that takes its own grant.
        if (!caller.MayStartBypass && UnsupervisedProfile.SkipsApprovals(allowedProfile.Defaults))
        {
            return new NodeStartCheck(null, $"The profile '{allowedProfile.Label}' skips its approvals, and this connect key does not have the mayStartBypassProfiles grant to start such a profile.");
        }

        if (projectId is { Length: > 0 } project && !caller.AllowsProject(project, pairing))
        {
            return new NodeStartCheck(null, caller.ByConnectKey
                ? $"The scope of this connect key does not include the project '{project}'. Call list_node_projects for the ones it does."
                : $"This node's operator has not allowed the project '{project}'. Call list_node_projects for the ones they have.");
        }

        // AC-1367: a key may start only what it could see afterwards, and a session without a project is visible
        // only to a key with every project. A pairing is not held to this (AC-795).
        if (projectId is not { Length: > 0 } && !caller.AllowsSession(allowedProfile.Label, null, pairing))
        {
            return new NodeStartCheck(null, "The scope of this connect key is limited to certain projects, so a start must name one of them. Call list_node_projects for the ones it does.");
        }

        return new NodeStartCheck(allowedProfile, null);
    }

    // Fail-closed: only a local model or a provider that reports an API key is shareable, so a kind the CLI has not
    // reported yet counts as a login.
    public static bool UsesPersonalLogin(SessionProfile profile, IProfileLoginChecker? logins) =>
        profile.ProviderConfig is not (OllamaConfig or LmStudioConfig)
        && logins?.CredentialKind(profile) is not (ProfileCredentialKind.ApiKey or ProfileCredentialKind.ApiKeyFromSecret);

    // The desk a caller's session lands on, derived here and never named by the caller — a controller has never seen
    // this cockpit's desks. Falls back to the first desk that can hold a session.
    public async Task<string?> ActiveWorkspaceIdAsync()
    {
        var workspaces = await gateway.ListWorkspacesAsync().ConfigureAwait(false);
        var usable = workspaces.Where(workspace => workspace.CanHostSessions).ToList();
        return (usable.FirstOrDefault(workspace => workspace.IsActive) ?? usable.FirstOrDefault())?.Id;
    }
}

// Either the profile a start may run under, or the refusal the caller is told.
internal sealed record NodeStartCheck(SessionProfile? Profile, string? Refusal);
