using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Mcp;

// Hands an agent its waiting mail on the result of a tool call it made itself (AC-527) — the one delivery route that
// works for every provider and every transport.
//
// <strong>Why here and not on a turn.</strong> Turn-start delivery (AC-394) is built on Cockpit's own SDK send
// funnel; a CLI in a terminal has no turn the host can add to, and neither will the next provider. What every one of
// them does have is this: they call `cockpit-*` MCP tools. A tool call is a moment where the agent is
// demonstrably running, the host composes the answer, and the agent is certain to read it — it asked for it. Nothing
// is pushed into a conversation and no turn is started, so none of the consent questions that surround an injected
// turn apply.
//
// <strong>Nothing waiting costs nothing.</strong> With an empty inbox the result is handed back as it came, the same
// object, untouched. That is the promise the whole form rests on — the cost of the line scales with mail, not with
// time — and it is why this reads the inbox rather than, say, always appending a "no messages" line.
//
// <strong>Delivered once, whichever route gets there first.</strong> This goes through the same
// `IAgentTurnInboxDelivery` as turn-start delivery, so it inherits that split: messages are taken
// in-flight, and only confirmed once they are actually in the result. A pane with both routes available cannot be
// handed the same message twice, because the second route finds nothing waiting — and if attaching fails, the batch
// goes back to waiting rather than vanishing with the sender told it arrived.
internal static class McpInboxPiggyback
{
    // Returns `result` with the caller's waiting mail attached, or exactly the object it was given
    // when there is none — no verified pane, no delivery service, an empty inbox, or anything at all going wrong.
    // Runs *after* the tool, deliberately. Reading the inbox first would let a `read_inbox` call return
    // nothing while this block carried what it should have handed over — the same messages, in the wrong half of the
    // answer, with the tool that exists to deliver them reporting an empty inbox.
    public static CallToolResult Attach(CallToolResult result, IAgentTurnInboxDelivery? delivery, ILogger logger)
    {
        // No verified pane means no inbox to read: the shared app-lifetime key path, or the in-process tool loop.
        // There is nobody to hand mail to and, as everywhere else on this line, nothing to guess from instead.
        if (delivery is null || McpRequestContext.CurrentPaneId is not { } paneId)
        {
            return result;
        }

        AgentInboxTurnNotice? notice = null;
        try
        {
            notice = delivery.TakeForTurn(paneId);
            if (notice is null)
            {
                return result;
            }

            // A fresh list rather than mutating the tool's own: the block is the host's addition to an answer the tool
            // composed, and a tool that hands back a cached or shared result must not find it changed underneath it.
            var content = new List<ContentBlock>(result.Content) { new TextContentBlock { Text = notice.Render(AgentInboxTurnNotice.ArrivedOnAToolResult) } };
            result.Content = content;

            delivery.ConfirmDelivered(notice);
            return result;
        }
        catch (Exception exception)
        {
            // The batch was taken and did not arrive, so it goes back to waiting, at the front. Losing mail while
            // telling its sender it was delivered is the one failure this whole line exists to avoid — and it must not
            // happen because a content list could not be built.
            if (notice is not null)
            {
                try
                {
                    delivery.ReturnUndelivered(notice);
                }
                catch (Exception returnFailure)
                {
                    logger.LogError(returnFailure, "Waiting messages for session {Pane} could not be returned to the inbox after a failed piggyback.", paneId);
                }
            }

            logger.LogWarning(exception, "Waiting messages for session {Pane} could not be attached to a tool result.", paneId);
            return result;
        }
    }
}
