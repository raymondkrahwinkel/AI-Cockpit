extern alias UiAsm;

using UiAsm::Cockpit.Plugin.YouTrack.UI;

namespace Cockpit.Plugin.YouTrack.Tests;

// `YouTrackDialogControl.BuildSearchTerm` (AC-518 follow-up): the server-side widen-search's query
// text — `#Unresolved` kept, the active state folded in when known, the free text quoted so a query-syntax
// character in it (a colon, a brace, a quote) reads as literal text rather than being parsed as one.
public class YouTrackSearchTermTests
{
    // EJ's board calls its status field "Kanban State" rather than "State" (StateFieldNames, YouTrackFieldParser):
    // "Kanban State: {Ready}" would read as two query tokens, so the field name gets the same {…} as the value.
    // The free text is quoted so a colon ("type: bug" as field:value) or braces stay literal, and a double quote or
    // backslash in it is escaped.
    [Theory]
    [InlineData("Kanban State", "Ready", "startup", "#Unresolved {Kanban State}: {Ready} \"startup\"")]
    [InlineData(null, null, "type: bug", "#Unresolved \"type: bug\"")]
    [InlineData(null, null, "{weird} value", "#Unresolved \"{weird} value\"")]
    [InlineData(null, null, "say \"hello\"", "#Unresolved \"say \\\"hello\\\"\"")]
    [InlineData(null, null, "C:\\path\\to\\file", "#Unresolved \"C:\\\\path\\\\to\\\\file\"")]
    public void BuildSearchTerm_BracesFieldsAndQuotesTheFreeTextLiterally(string? fieldName, string? state, string query, string expected) =>
        Assert.Equal(expected, YouTrackDialogControl.BuildSearchTerm(fieldName, state, query));
}
