using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Mcp;

// AC-527: hands an agent its waiting mail on the result of a tool call it made itself — the one delivery route
// that works for every transport, since turn-start delivery (AC-394) needs a turn a CLI doesn't have. Shares
// `IAgentTurnInboxDelivery` with turn-start delivery so a message is never delivered twice.
internal static class McpInboxPiggyback
{
    // Returns `result` with waiting mail attached, or the same object untouched when there's none. Runs after
    // the tool deliberately — reading first would let a `read_inbox` call race this and report an empty inbox.
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
