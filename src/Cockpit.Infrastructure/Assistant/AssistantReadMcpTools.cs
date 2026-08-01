using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Formatting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// The <c>cockpit-assistant</c> MCP tools (AC-544): the voice assistant's read path over every session in every
/// workspace. Reading only — acting is <c>[c]</c>'s, and nothing here changes anything.
/// </summary>
/// <remarks>
/// <b>Why this is a separate server and not two more tools on <c>cockpit-agents</c>.</b> That server's tools are
/// workspace-scoped by construction: they derive the caller's desk host-side from the transport-verified pane and
/// refuse a request they cannot place on one. The assistant is placed on no desk at all, so the cheapest way to
/// make it work there would be to relax that derivation — and the derivation is not a filter on one tool, it is the
/// reason no agent anywhere can reach another workspace's roster. Loosening it to serve one privileged caller
/// removes the protection for every other caller at the same time, silently, and it compiles.
/// <para>
/// So the broad reach lives here instead, behind two independent gates:
/// </para>
/// <para>
/// <b>1. It is not handed out.</b> The endpoint is registered <c>Internal</c> (AC-204), which keeps it out of every
/// user-facing MCP picker <em>and</em> out of the no-selection fan-out, so it reaches only a launch that names it —
/// and the assistant's own start is the one place in the codebase that does.
/// </para>
/// <para>
/// <b>2. It is not answered.</b> Every tool here refuses any caller whose verified pane is not
/// <see cref="AssistantIdentity.PaneId"/>. That is the gate that actually holds, and it is the one worth having:
/// the mount is a fact about configuration, and configuration is exactly the kind of thing that widens later by
/// accident — an endpoint made non-internal, a profile that names the server, a spawn path that copies a selection
/// it did not read. When that happens the tools are in a session's context and still answer nobody. The pane is
/// stamped by <see cref="McpAuthMiddleware"/> from the request's own per-session bearer and no argument on any tool
/// here can move it, so "I am the assistant" is not a sentence a session can say.
/// </para>
/// <para>
/// <b>Where that stops</b> is where AC-89's per-session tokens stop, and no further: every session runs as the same
/// OS user, so an agent with a shell can read a neighbour's <c>COCKPIT_MCP_KEY</c> out of its environment and send
/// as it. That is a property the whole cockpit shares — the consent broker and the agent line included — and it is
/// not fixable from here. What this design buys is that reaching these tools takes deliberate theft off the
/// filesystem rather than a tool argument or an unticked checkbox.
/// </para>
/// </remarks>
internal sealed class AssistantReadMcpTools(IAssistantReadGateway gateway)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// What a caller that is not the assistant is told. One sentence, and no detail about what it would have got:
    /// the refusal is the whole answer, and there is nothing here for an ordinary session to learn from.
    /// </summary>
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "list_sessions")]
    [Description("Lists every AI session the cockpit is running right now, across all workspaces — not just one desk. Each entry has the pane id, the session's name, the profile it runs under, the workspace it sits on (id and the tab label the operator sees), and its statusline: whatever that session last set for itself with cockpit-session__set_status. Use it to answer questions like \"what is the status of AC-223\" or \"what is everyone working on\". IMPORTANT about what a statusline is and is not: it is a convention, not a record. A session says what it is working on because it was asked to, so a statusline mentioning a ticket is good evidence that session is on it — but a ticket appearing nowhere means only that no running session has written that ticket into its own status line. It does NOT mean nobody is working on it: a session may never have set a status, may have set a stale one, or may be doing the work under a different description. There is also one whole class of worker this list cannot see at all — a delegated task (delegate_task) runs without a pane and therefore without a statusline, so it never appears here however busy it is. Report the difference rather than turning an absence of evidence into an answer.")]
    public async Task<string> ListSessionsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var sessions = await gateway.ListSessionsAsync().ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                count = sessions.Count,
                sessions = sessions.Select(session => new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    workspaceId = session.WorkspaceId,
                    workspaceName = session.WorkspaceName,
                    statusline = session.Statusline,
                    // Said in the row rather than left for the reader to infer from an empty string. An empty
                    // statusline and a session working quietly look identical from here, and the field that says so
                    // is cheaper than the mistake it prevents.
                    hasStatusline = session.Statusline.Length > 0,
                }),
            });
        }
        catch (Exception exception)
        {
            // A tool result, never an MCP protocol error — the same choice cockpit-agents makes, so an unexpected
            // failure here does not look to the assistant's runtime like the transport itself broke.
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    /// <summary>
    /// How much of a transcript one <c>read_transcript</c> hands over when the caller does not say.
    /// <para>
    /// Thirty rows, because a row is not a turn: one turn of an agent is typically a user line, a thinking block, a
    /// handful of tool calls and a closing paragraph, so thirty rows is the last few turns — which is the span that
    /// answers what this ticket asks a transcript for ("what is it doing", "where did it get stuck", "did it ever
    /// hear me"). It is a default for a spoken question, and a spoken question is nearly always about the recent
    /// end. The bound exists because the alternative is not "a longer answer" but a whole session pulled into every
    /// turn of the assistant's own context, priced per token, for a question that wanted the last thing that
    /// happened.
    /// </para>
    /// </summary>
    internal const int DefaultEntryCount = 30;

    /// <summary>
    /// The ceiling <c>count</c> cannot be raised past, however large a number is passed.
    /// <para>
    /// The parameter exists for the case the default really is too narrow — "read further back, it started before
    /// that" — and the ceiling exists because that request arrives as a number chosen by a model, which has no way to
    /// know what it costs. Clamped rather than refused: a caller asking for a thousand wants as much as it can have,
    /// and <c>omitted</c> in the reply already tells it what it did not get, so a refusal would only cost a
    /// round-trip to arrive at the same hundred rows.
    /// </para>
    /// </summary>
    internal const int MaxEntryCount = 100;

    /// <summary>
    /// The most of any single transcript row that is repeated into the assistant's context.
    /// <para>
    /// Bounding the row <em>count</em> does not bound the byte count, and on a transcript the gap is not theoretical:
    /// one tool result — a file read, a build log, a <c>git diff</c> — is routinely larger than every other row put
    /// together, and nothing stops it being ten megabytes. Thirty rows of that is a session-ending read of a tool
    /// whose entire purpose is to answer a question about somebody else's session. The same 2000 characters an agent
    /// message body is held to (<see cref="AgentMessageContent.MaxBodyLength"/>), for the same reason and with the
    /// same arithmetic: it caps one full read at roughly 200 000 characters at the ceiling, and 60 000 at the
    /// default. Truncated rather than refused, unlike a message body — there is nobody to hand the refusal to who
    /// could shorten it, and the first 2000 characters of a build log is the half that says what failed.
    /// </para>
    /// </summary>
    internal const int MaxEntryTextLength = 2000;

    [McpServerTool(Name = "read_transcript")]
    [Description("Reads the raw transcript of one AI session, named by its pane id — any session in any workspace, not just one desk. Take the pane id from list_sessions. Returns the entries as they happened, oldest first: each has a kind (UserText, AssistantText, ToolUse, ToolResult, Thinking, Question, Error, TurnCompleted), the text of the row, and — on a tool call — the result that call returned. It is passed through raw and unedited, exactly as the operator's own screen shows it; reading it, making sense of it and saying what it means in a sentence is your job, not the cockpit's. BOUNDED: by default you get the last 30 entries, not the whole session, which is the recent end where nearly every spoken question is actually pointed. The reply always says totalEntries and omitted, so you can tell a short session from a long one you only saw the tail of — never report a session as having started with what is simply the first line you were given. Ask for more with count (up to 100) only when the question really is about earlier on, e.g. \"what did it try before that\". A single very long entry is cut to 2000 characters and marked truncated: that is a shortened tool result, not a complete one.")]
    public async Task<string> ReadTranscriptAsync(
        [Description("The pane id of the session to read, exactly as list_sessions reports it. There is no name lookup here: find the session with list_sessions first, then read the pane it names.")] string paneId,
        [Description("How many of the most recent entries to return. Defaults to 30 and is capped at 100 — a larger number is quietly clamped, not refused. Raise it only when the question is about earlier in the session; a wider read costs context on every turn that follows it.")] int count = DefaultEntryCount)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var transcript = await gateway.ReadTranscriptAsync(paneId, Math.Clamp(count, 1, MaxEntryCount))
                .ConfigureAwait(false);
            if (transcript is null)
            {
                // Said plainly, and without guessing at what was meant: this tool takes a pane id, and a pane id that
                // matches nothing is either a session that has since closed or one of the cockpit's plain terminals,
                // which has no agent behind it to have a transcript. Neither is worth a search over session names —
                // list_sessions is right there, and it is the half that knows what the operator calls things.
                return _Serialize(new
                {
                    ok = false,
                    error = $"No AI session is running on pane '{paneId}'. It may have closed, or the pane may be a "
                        + "plain terminal rather than an agent. Call list_sessions for the panes that exist now.",
                });
            }

            var entries = transcript.Entries.Select(entry =>
            {
                var (text, textTruncated) = _Bounded(entry.Text);
                var (result, resultTruncated) = _Bounded(entry.ToolResult);
                return new
                {
                    kind = entry.Kind,
                    text,
                    toolResult = entry.ToolResult is null ? null : result,
                    // Per entry rather than once for the whole read: "something in here was shortened" would leave the
                    // reader unable to tell which tool result it may quote as complete.
                    truncated = textTruncated || resultTruncated,
                };
            }).ToArray();

            return _Serialize(new
            {
                ok = true,
                paneId = transcript.PaneId,
                name = transcript.Name,
                count = entries.Length,
                totalEntries = transcript.TotalEntries,
                // What was left out in front of this slice. A capped read has to say so, or a tail is indistinguishable
                // from a whole session and the assistant reports a beginning that is not one — the same field, and the
                // same reasoning, as read_inbox's `remaining`.
                omitted = transcript.TotalEntries - entries.Length,
                more = transcript.TotalEntries > entries.Length
                    ? $"This is the last {entries.Length} of {transcript.TotalEntries} entries — {transcript.TotalEntries - entries.Length} earlier ones were not read. Ask again with a larger count (up to {MaxEntryCount}) if the question is about earlier on."
                    : null,
                entries,
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    /// <summary>
    /// One transcript row as the assistant may be shown it: terminal control sequences stripped and cut to
    /// <see cref="MaxEntryTextLength"/>, with whether the cut actually happened.
    /// </summary>
    /// <remarks>
    /// The stripping is not cosmetic and it is not this tool's invention — it is the same
    /// <see cref="AgentMessageContent.Normalize"/> every agent-authored line already passes through before it is
    /// repeated into another session's context. A transcript is the most agent-authored text there is, and it ends up
    /// in a reply the assistant's own runtime prints, so a tool result full of ANSI would otherwise be able to
    /// reposition a cursor or overwrite what the cockpit wrote above it. Reused rather than rewritten: a second
    /// implementation of "which control characters are dangerous" is a second one to get wrong.
    /// <para>
    /// Truncation is reported off the normalised length, not the raw one, so a row that merely had trailing
    /// whitespace stripped is not announced as shortened.
    /// </para>
    /// </remarks>
    private static (string Text, bool Truncated) _Bounded(string? text)
    {
        var normalized = AgentMessageContent.Normalize(text, out _);
        return (BoundedText.Trim(normalized, MaxEntryTextLength), normalized.Length > MaxEntryTextLength);
    }

    /// <summary>
    /// The gate, in one place so every tool on this server is covered by the same sentence rather than by its own
    /// copy of it. Returns the refusal to hand straight back, or null when the caller really is the assistant.
    /// </summary>
    /// <remarks>
    /// A request with no verified pane is refused too, and not because it might be an impostor: it is the shared
    /// app-lifetime key path (the in-process tool loop), which cannot be attributed to any session at all. There is
    /// no identity to check, so there is no way to establish this one — and the safe answer to "I cannot tell who
    /// this is" on a tool that reads every workspace is no.
    /// </remarks>
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
