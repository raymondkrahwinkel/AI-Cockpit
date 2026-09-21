namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1339: extracting a ticket's own acceptance-criteria section, so a Ready epic-sub's auto-submitted plan is
// judged against the ticket's own words rather than the CEO's paraphrase of them.
public class AutopilotAcceptanceSectionTests
{
    private static readonly string[] Headings = ["Acceptatiecriteria", "Acceptance criteria"];

    public static IEnumerable<object[]> Descriptions() =>
    [
        // Bold heading, with the ticket's own "(tegenproef)" suffix before the colon — still matched as a prefix
        // — and a following bold heading of the same kind that must NOT be swallowed into the section.
        [
            "Intro text.\n\n**Acceptatiecriteria (tegenproef):**\n- Sub met X → Y.\n- Sub zonder → Z.\n\n**Testbudget:** 4.",
            "- Sub met X → Y.\n- Sub zonder → Z.",
        ],
        // Markdown ATX heading, stopping at the next markdown heading.
        [
            "## Acceptance criteria\nDo the thing.\n\n## Notes\nIgnored.",
            "Do the thing.",
        ],
        // No heading of either kind — nothing to extract.
        [
            "Just a description with no structured sections at all.",
            "",
        ],
    ];

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Extract_ReadsTheSectionBody_ForBothHeadingStyles_OrEmptyWhenAbsent(string description, string expected) =>
        Assert.Equal(expected, AutopilotAcceptanceSection.Extract(description, Headings));
}
