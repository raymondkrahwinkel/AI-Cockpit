using Cockpit.Core.Mentions;

namespace Cockpit.Core.Tests.Mentions;

/// <summary>
/// Token detection for the AC-740 @-mention picker: which caret positions count as "inside a mention",
/// and which look like one but must not trigger (an email address, an '@' typed mid-word).
/// </summary>
public class MentionQueryTests
{
    [Fact]
    public void From_AtTheStartOfTheText_TriggersWithEmptyQuery() =>
        Assert.Equal((0, ""), MentionQuery.From("@", 1));

    [Fact]
    public void From_AtTheStartWithQueryText_TriggersWithThatQuery() =>
        Assert.Equal((0, "foo"), MentionQuery.From("@foo", 4));

    [Fact]
    public void From_AfterWhitespace_Triggers() =>
        Assert.Equal((6, "wor"), MentionQuery.From("hello @wor", 10));

    [Fact]
    public void From_AfterANewline_Triggers() =>
        Assert.Equal((6, "wor"), MentionQuery.From("hello\n@wor", 10));

    [Fact]
    public void From_MidWord_DoesNotTrigger() =>
        Assert.Null(MentionQuery.From("foo@bar", 7));

    [Fact]
    public void From_AnEmailAddress_DoesNotTrigger() =>
        Assert.Null(MentionQuery.From("mail user@example.com", 22));

    [Fact]
    public void From_MultipleAts_UsesTheNearestOneToTheCaret() =>
        Assert.Equal((5, "bar"), MentionQuery.From("@foo @bar", 9));

    [Fact]
    public void From_AnAtNestedInAWord_DoesNotTrigger() =>
        // The nearest '@' to the caret is preceded by 'b', not whitespace or start-of-text.
        Assert.Null(MentionQuery.From("a@b@c", 5));

    [Fact]
    public void From_CaretBeforeTheAt_DoesNotTrigger() =>
        Assert.Null(MentionQuery.From("@foo", 0));

    [Fact]
    public void From_WhitespaceBetweenAtAndCaret_DoesNotTrigger() =>
        // A space right after '@' closes the token — typing on past it never re-opens for what follows.
        Assert.Null(MentionQuery.From("@ 5", 3));

    [Fact]
    public void From_EmptyText_ReturnsNull() =>
        Assert.Null(MentionQuery.From("", 0));

    [Fact]
    public void From_CaretAtTheVeryStart_ReturnsNull() =>
        Assert.Null(MentionQuery.From("@foo", 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void From_CaretOutOfRange_ReturnsNull(int caretIndex) =>
        Assert.Null(MentionQuery.From("@foo", caretIndex));

    [Fact]
    public void From_CaretMidwayThroughTheQuery_UsesOnlyTheTextBeforeTheCaret() =>
        Assert.Equal((0, "fo"), MentionQuery.From("@foo", 3));
}
