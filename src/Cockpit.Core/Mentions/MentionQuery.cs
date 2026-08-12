namespace Cockpit.Core.Mentions;

// Finds the @-mention token the caret currently sits inside, if any. Deliberately conservative: only an
// unbroken run of non-whitespace after an '@' that starts the text or follows whitespace counts, so
// "user@example.com" never triggers it, and a space right after '@' closes the token before it can reopen.
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
