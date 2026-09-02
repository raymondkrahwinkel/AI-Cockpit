namespace Cockpit.Plugin.Autopilot.Tests;

// The template placeholder resolver (AC-189): it fills the `{{issue.*}}` tokens from a tracker intent's data and
// the `{{input.*}}` tokens from operator input, in one pass, and never throws — a missing or unknown token
// becomes the empty string and is reported so the surface can warn.
public class AutopilotTemplateResolverTests
{
    private static Dictionary<string, string> _IntentData() => new()
    {
        ["tracker"] = "youtrack",
        ["issue"] = "AC-189",
        ["title"] = "Autopilot templates",
        ["description"] = "Build the template foundation.",
        ["url"] = "https://youtrack/AC-189",
    };

    public static IEnumerable<object[]> BodiesThatResolveWhole() =>
    [
        // Every {{issue.*}} token, filled from the intent data.
        [
            "{{issue.tracker}} {{issue.id}}: {{issue.title}}\n{{issue.description}}\n{{issue.url}}",
            _IntentData(), null!,
            "youtrack AC-189: Autopilot templates\nBuild the template foundation.\nhttps://youtrack/AC-189",
        ],
        // The {{input.*}} tokens, filled from operator input.
        [
            "Work on {{input.branch}}, ask {{input.reviewer}}.", null!,
            new Dictionary<string, string> { ["branch"] = "feat/AC-189", ["reviewer"] = "Zyra" },
            "Work on feat/AC-189, ask Zyra.",
        ],
        // Whitespace inside the braces is tolerated on both kinds of token.
        [
            "{{ issue.id }} / {{  input.branch  }}", _IntentData(),
            new Dictionary<string, string> { ["branch"] = "b" }, "AC-189 / b",
        ],
        // A blank description is a value the intent carried, not an absent one — the key was there, so it resolves.
        ["[{{issue.description}}]", new Dictionary<string, string> { ["description"] = string.Empty }, null!, "[]"],
        // Text that is not a {{token}} — including a lone brace or a C#-style interpolation — passes through untouched.
        [
            "Ship it. Cost {price} and {{issue.id}} only.", new Dictionary<string, string> { ["issue"] = "AC-189" }, null!,
            "Ship it. Cost {price} and AC-189 only.",
        ],
    ];

    [Theory]
    [MemberData(nameof(BodiesThatResolveWhole))]
    public void Resolve_FillsEveryTokenItKnows_AndReportsNothingMissing(
        string body, Dictionary<string, string>? intentData, Dictionary<string, string>? input, string expected)
    {
        var result = AutopilotTemplateResolver.Resolve(body, intentData, input);

        Assert.Equal(expected, result.Text);
        Assert.Empty(result.MissingPlaceholders);
    }

    public static IEnumerable<object[]> BodiesWithGaps() =>
    [
        // issue.url is absent from the data, input.branch has no input, and foo.bar is not a token the resolver knows —
        // all three become empty and are reported, and none of them throws.
        [
            "{{issue.id}}|{{issue.url}}|{{input.branch}}|{{foo.bar}}",
            new Dictionary<string, string> { ["issue"] = "AC-189" }, null!,
            "AC-189|||", new[] { "issue.url", "input.branch", "foo.bar" },
        ],
        // Each missing name is reported once, in first-seen order — the exact list, so a repeat would show up here.
        ["{{input.x}} {{input.y}} {{input.x}}", null!, null!, "  ", new[] { "input.x", "input.y" }],
    ];

    [Theory]
    [MemberData(nameof(BodiesWithGaps))]
    public void Resolve_AGapBecomesEmptyAndIsReportedOnce_WithoutThrowing(
        string body, Dictionary<string, string>? intentData, Dictionary<string, string>? input, string expected, string[] missing)
    {
        var result = AutopilotTemplateResolver.Resolve(body, intentData, input);

        Assert.Equal(expected, result.Text);
        Assert.Equal(missing, result.MissingPlaceholders);
    }
}
