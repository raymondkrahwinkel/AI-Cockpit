using System.Text;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: AC-392 normalises then bounds a notify's free-text args before they reach a tool result, the
// trail, or (AC-394) a recipient's turn — the sender is an untrusted agent. Does not make a body safe to
// obey; defence is provenance-labelling (`AgentsMcpTools.InboxOrigin`), not sanitisation — a known residual risk.
internal static class AgentMessageContent
{
    // A label, so a bound that fits a label and not a paragraph — anything longer is a body in the wrong field. Matches
    // what the trail keeps, so a kind is never trimmed on its way to disk.
    internal const int MaxKindLength = 100;

    // AC-1013: a paragraph or two, not a document (point at a shared file for more). Bounds the inbox in bytes,
    // not just count: caps unread mail at ~2MB (`MaxWaitingPerPane`x) and one read at 50k chars (`MaxMessagesPerRead`x).
    internal const int MaxBodyLength = 2000;

    // A pane id the host minted is 32 hex characters, so this never touches a real one. What it bounds is a refused
    // attempt, where the addressee is a string the sending agent chose: it is echoed back in the refusal and written to
    // the trail, and neither should be able to carry a megabyte because a sender addressed a megabyte.
    internal const int MaxPaneIdLength = 200;

    // AC-1013: strips C0 (except tab/newline), DEL, and C1 (U+0080-U+009F) — the machinery of terminal escape
    // sequences (ESC, CSI, cursor-repositioning CR) — so a displayed/replayed body can't repaint another session's
    // output. `text` may be null (explicit JSON null); `removedControlCharacters` tells the sender it was altered.
    internal static string Normalize(string? text, out bool removedControlCharacters)
    {
        removedControlCharacters = false;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? kept = null;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (!_IsStripped(character))
            {
                kept?.Append(character);
                continue;
            }

            // Built only once something has to be dropped, so the common case (text with nothing wrong with it)
            // allocates nothing beyond the trim below.
            kept ??= new StringBuilder(text.Length).Append(text, 0, i);
            removedControlCharacters = true;
        }

        return (kept?.ToString() ?? text).Trim();
    }

    // Why this message may not be sent, in the sender's own terms — or null when it may. Runs on already-normalised
    // text, so "empty" means empty after the stripping above: a body that was nothing but control characters is a body
    // the recipient cannot read, and is refused rather than delivered blank.
    internal static string? Reject(string toPaneId, string kind, string body) => toPaneId switch
    {
        { Length: 0 } => "No addressee. Pass the pane id of the agent to notify as `toPaneId` — take it from list_agents.",
        { } id when id.Length > MaxPaneIdLength =>
            $"That is not a pane id: `toPaneId` is {id.Length} characters and a pane id is far shorter than the {MaxPaneIdLength}-character limit. Take it from list_agents.",
        _ => kind switch
        {
            { Length: 0 } => "No kind. Pass a short label for what this message is, e.g. 'question', 'heads-up' or 'handover'.",
            { } label when label.Length > MaxKindLength =>
                $"`kind` is {label.Length} characters and the limit is {MaxKindLength}. It is a short label — put the message itself in `body`.",
            _ => body switch
            {
                { Length: 0 } => "No body. Pass the message itself — an empty one costs the recipient a turn and tells it nothing.",
                { } text when text.Length > MaxBodyLength =>
                    $"`body` is {text.Length} characters and the limit is {MaxBodyLength}. Nothing was sent — shorten it, or point at a file the recipient can read for itself.",
                _ => null,
            },
        },
    };

    private static bool _IsStripped(char character)
    {
        // The two an author meant, matched first so the C0 range below does not take them with it.
        if (character is '\t' or '\n')
        {
            return false;
        }

        // Written as code points rather than char literals, so the source of a control-character filter holds no
        // control characters itself: C0 (where ESC 0x1B and the bare CR live), DEL, and C1 — whose CSI (0x9B) starts an
        // escape sequence with no ESC in front of it at all.
        var code = (int)character;
        return code < 0x20 || code == 0x7F || code is >= 0x80 and <= 0x9F;
    }
}
