namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side read path the assistant sees the whole cockpit through (AC-544): every AI session, on every
/// workspace, with the statusline it last set for itself.
/// </summary>
/// <remarks>
/// <b>Why this is not <c>list_agents</c>.</b> <c>cockpit-agents</c>' roster is workspace-scoped on purpose, and the
/// scoping is not a filter it applies — it is the shape of the thing: the workspace is derived host-side from the
/// transport-verified pane the request came from, so there is nothing an agent could declare to reach another
/// workspace's roster. The assistant has no workspace at all (<c>SessionWorkspacePlacement</c> places it nowhere,
/// deliberately), which means that tool cannot answer for it — and the short way to make it answer would be to
/// loosen the derivation, which removes the protection for every agent in the cockpit, not just for this one.
/// <para>
/// So this is a second, separate read path that stands above the workspaces instead of inside one. Same source —
/// the running session panels — same fields, no new store and no new index: the statuslines are already kept, and
/// "who is on AC-223" is a question about data the cockpit already holds. What is new is only a reader that is not
/// standing on a desk.
/// </para>
/// <para>
/// <b>And it is not reachable from an ordinary session.</b> The tools over this interface are mounted only into the
/// assistant's own launch, and refuse any caller whose verified pane is not
/// <see cref="Core.Assistant.AssistantIdentity.PaneId"/>. Exclusion by construction, twice over: not handed out,
/// and not answered even when it is. See <c>AssistantReadMcpTools</c>.
/// </para>
/// </remarks>
public interface IAssistantReadGateway
{
    /// <summary>
    /// Every AI session the cockpit is running right now, across every workspace, in no particular order.
    /// <para>
    /// Whole rather than searched: the answer is a handful of rows, the caller is a model that reads them anyway,
    /// and a query parameter here would be a second place where "does this session match AC-223" is decided — one
    /// that would match on exact text while the assistant is the half that can read through a statusline saying
    /// "ac223 tests" and see the same ticket.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync();
}

/// <summary>
/// One running session as the assistant is shown it: enough to answer "who is working on this, and where", and
/// nothing more.
/// </summary>
/// <param name="PaneId">The session's pane id — its handle, and what a later phase would act on.</param>
/// <param name="Name">The session's title, as the operator sees it in the sidebar.</param>
/// <param name="Profile">The profile it runs under, or empty when it has not reported one yet.</param>
/// <param name="Statusline">
/// What the session last said it is working on with <c>cockpit-session__set_status</c>, or empty when it has never
/// said anything. Empty is the ordinary case for a session nobody instructed, and it is exactly why the assistant
/// is told never to read an absence here as an absence of work — see <c>AssistantSystemPrompt.Default</c>.
/// </param>
/// <param name="WorkspaceId">The workspace it sits on, or null for a session the cockpit places on no desk.</param>
/// <param name="WorkspaceName">
/// That workspace's tab label — the name the operator would actually recognise, since the id is never shown
/// anywhere. Null when there is no workspace, and null rather than the id when the workspace has since gone.
/// </param>
public sealed record AssistantSessionRow(
    string PaneId,
    string Name,
    string Profile,
    string Statusline,
    string? WorkspaceId,
    string? WorkspaceName);
