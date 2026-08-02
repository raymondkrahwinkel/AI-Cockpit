namespace Cockpit.Plugin.YouTrack.Tests;

// `YouTrackDialogControl.BuildSearchTerm` (AC-518 follow-up): the server-side widen-search's query
// text — `#Unresolved` kept, the active state folded in when known, the free text quoted so a query-syntax
// character in it (a colon, a brace, a quote) reads as literal text rather than being parsed as one.
public class YouTrackSearchTermTests
{
    [Fact]
    public void BuildSearchTerm_WithNoActiveState_QuotesThePlainQuery()
    {
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "startup");

        Assert.Equal("#Unresolved \"startup\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithAnActiveState_FoldsItInBeforeTheQuery()
    {
        // Requirement 2 (Raymond): a widen search must not surface issues from a stage the state filter excludes —
        // the same two-truths mistake AC-518's own state filter guarded against.
        var term = YouTrackDialogControl.BuildSearchTerm("State", "In Progress", "startup");

        Assert.Equal("#Unresolved State: {In Progress} \"startup\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithStateSetToAll_OmitsTheStateTerm()
    {
        var term = YouTrackDialogControl.BuildSearchTerm("State", "All", "startup");

        Assert.Equal("#Unresolved \"startup\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithAMultiWordFieldName_BracesTheFieldNameTooNotJustTheValue()
    {
        // EJ's board calls its status field "Kanban State" rather than "State" (StateFieldNames, YouTrackFieldParser).
        // Only the value was ever braced here — "Kanban State: {Ready}" reads as two query tokens ("Kanban" bare,
        // then "State: {Ready}"), not the one field:value pair intended. The field name needs the same {…} the
        // value already gets whenever it is not a single bare word.
        var term = YouTrackDialogControl.BuildSearchTerm("Kanban State", "Ready", "startup");

        Assert.Equal("#Unresolved {Kanban State}: {Ready} \"startup\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithSpacesInTheQuery_StaysOneQuotedPhrase()
    {
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "slow cold start");

        Assert.Equal("#Unresolved \"slow cold start\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithAColonInTheQuery_StaysLiteralRatherThanBecomingFieldSyntax()
    {
        // Unquoted, "type: bug" risks being parsed as a field:value expression rather than free text.
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "type: bug");

        Assert.Equal("#Unresolved \"type: bug\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithBracesInTheQuery_StaysInsideTheOuterQuotesUnescaped()
    {
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "{weird} value");

        Assert.Equal("#Unresolved \"{weird} value\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithADoubleQuoteInTheQuery_EscapesIt()
    {
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "say \"hello\"");

        Assert.Equal("#Unresolved \"say \\\"hello\\\"\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithABackslashInTheQuery_EscapesIt()
    {
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, "C:\\path\\to\\file");

        Assert.Equal("#Unresolved \"C:\\\\path\\\\to\\\\file\"", term);
    }

    [Fact]
    public void BuildSearchTerm_WithUnicode_PreservesItExactly()
    {
        const string odd = "Ünïcödé ✅ — Réview";
        var term = YouTrackDialogControl.BuildSearchTerm(null, null, odd);

        Assert.Equal($"#Unresolved \"{odd}\"", term);
    }
}
