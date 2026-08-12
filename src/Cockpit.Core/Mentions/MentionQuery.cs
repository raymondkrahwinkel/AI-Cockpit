namespace Cockpit.Core.Mentions;

// Finds the @-mention token the caret currently sits inside, if any — the pure detection step behind AC-740's
// file-/folder-picker. Deliberately conservative: only a caret-driven, unbroken run of non-whitespace after an
// '@' that starts the text or follows whitespace counts as a token, so "user@example.com" and a lone "@" typed
// mid-sentence never trigger it, and a space right after '@' closes the token before it can reopen.
public static class MentionQuery
{
    // The token the caret sits in, as (start index of the '@', query text after it) — or null when the caret
    // isn't inside a triggerable mention. `query` excludes the '@' itself.
    public static (int Start, string Query)? From(string text, int caretIndex)
    {
        if (string.IsNullOrEmpty(text) || caretIndex <= 0 || caretIndex > text.Length)
        {
            return null;
        }

        var at = -1;
        for (var i = caretIndex - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return null;
            }

            if (text[i] == '@')
            {
                at = i;
                break;
            }
        }

        if (at < 0 || (at > 0 && !char.IsWhiteSpace(text[at - 1])))
        {
            return null;
        }

        return (at, text[(at + 1)..caretIndex]);
    }
}
