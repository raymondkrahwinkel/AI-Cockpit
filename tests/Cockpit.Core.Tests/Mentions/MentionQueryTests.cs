using Cockpit.Core.Mentions;

namespace Cockpit.Core.Tests.Mentions;

/// <summary>
/// Token detection for the AC-740 @-mention picker: which caret positions count as "inside a mention",
/// and which look like one but must not trigger (an email address, an '@' typed mid-word).
/// </summary>
public class MentionQueryTests
{
    [Theory]
    [InlineData("@", 1, 0, "")] // the '@' has just been typed
    [InlineData("@foo", 4, 0, "foo")]
    [InlineData("hello @wor", 10, 6, "wor")] // after whitespace
    [InlineData("hello\n@wor", 10, 6, "wor")] // after a newline
    [InlineData("@foo @bar", 9, 5, "bar")] // the nearest '@' to the caret wins
    [InlineData("@foo", 3, 0, "fo")] // only the text before the caret is the query
    public void From_ACaretInsideAMention_ReturnsThatTokenAndItsQuery(
        string text, int caretIndex, int expectedStart, string expectedQuery)
    {
        Assert.Equal((expectedStart, expectedQuery), MentionQuery.From(text, caretIndex));
    }

    [Theory]
    [InlineData("foo@bar", 7)] // mid-word
    [InlineData("mail user@example.com", 22)] // an email address
    [InlineData("a@b@c", 5)] // the nearest '@' is preceded by 'b', not whitespace or start-of-text
    [InlineData("hi @foo", 2)] // the '@' is ahead of the caret, so it is not the caret's mention
    [InlineData("@ 5", 3)] // a space right after '@' closes the token and never re-opens
    [InlineData("", 0)]
    [InlineData("@foo", 0)] // caret at the very start
    [InlineData("@foo", -1)] // caret out of range
    [InlineData("@foo", 100)]
    public void From_WhatOnlyLooksLikeAMention_DoesNotTrigger(string text, int caretIndex)
    {
        Assert.Null(MentionQuery.From(text, caretIndex));
    }
}
