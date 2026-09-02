namespace Cockpit.Plugin.Autopilot.Tests;

// The plan-flow kickoff (AC-189, slice 3): the operator's template choice becomes the CEO's opening turn. A chosen
// template's body (its {{issue.*}} tokens filled) replaces the hardcoded source kickoff; no template keeps the
// current behaviour. Plan source and template are internal records (CS0051), so the rows box them.
public class AutopilotTemplateKickoffTests
{
    private static readonly AutopilotPlanSource _Source =
        new("youtrack", "AC-138", "Reading levels", "Add reading levels to the chat view.", "https://youtrack.example/issue/AC-138");

    public static IEnumerable<object[]> KickoffsWithoutATemplate() =>
    [
        // A tracker-triggered run keeps the hardcoded source kickoff it had before templates existed.
        [_Source, AutopilotCeoBrief.SourceKickoff(_Source)],
        // A CEO-first run has nothing to open with, so the CEO is left idle to ask the operator itself.
        [null!, null!],
    ];

    [Theory]
    [MemberData(nameof(KickoffsWithoutATemplate))]
    public void NoTemplate_KeepsWhateverTheSourceAlreadyGave(object? source, string? expected)
    {
        var kickoff = AutopilotTemplateKickoff.Build(template: null, (AutopilotPlanSource?)source);

        Assert.Equal(expected, kickoff.Message);
        Assert.Empty(kickoff.MissingPlaceholders);
    }

    public static IEnumerable<object[]> KickoffsFromATemplate() =>
    [
        // The body resolves from the source, and that resolved text is the opening turn.
        [
            AutopilotTemplate.ForPlugin("youtrack", new(
                "youtrack.bugfix", "Bug fix",
                "Fix {{issue.id}}: \"{{issue.title}}\" on {{issue.tracker}}. {{issue.description}}")),
            _Source,
            "Fix AC-138: \"Reading levels\" on youtrack. Add reading levels to the chat view.",
            Array.Empty<string>(),
        ],
        // The source now carries the item's url (AC-189), so {{issue.url}} resolves to the real link and is no longer
        // reported missing — the gebrek where it always resolved empty. Only {{input.branch}} is left blank.
        [
            AutopilotTemplate.ForPlugin("youtrack", new("t", "T", "Fix {{issue.id}} at {{issue.url}} on branch {{input.branch}}.")),
            _Source,
            "Fix AC-138 at https://youtrack.example/issue/AC-138 on branch .",
            new[] { "input.branch" },
        ],
        // A CEO-first run (no source) with an issue-only template resolves to whitespace; that must not submit an
        // empty turn — leave the CEO idle so it asks the operator what the run should achieve.
        [AutopilotTemplate.ForUser("u", "U", "{{issue.title}}"), null!, null!, new[] { "issue.title" }],
    ];

    [Theory]
    [MemberData(nameof(KickoffsFromATemplate))]
    public void ChosenTemplate_ResolvesItsBodyIntoTheKickoff_ReportingWhatItCouldNotFill(
        object template, object? source, string? expected, string[] missing)
    {
        var kickoff = AutopilotTemplateKickoff.Build((AutopilotTemplate)template, (AutopilotPlanSource?)source);

        Assert.Equal(expected, kickoff.Message);
        Assert.Equal(missing, kickoff.MissingPlaceholders);
    }

    [Fact]
    public void SourceData_CarriesTheUrl_KeyedTheWayTheResolverExpects()
    {
        var data = AutopilotTemplateKickoff.SourceData(_Source);

        Assert.NotNull(data);
        Assert.Equal("https://youtrack.example/issue/AC-138", data["url"]);
    }
}
