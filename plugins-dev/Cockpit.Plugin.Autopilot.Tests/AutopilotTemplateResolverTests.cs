using Cockpit.TestSupport;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The template placeholder resolver (AC-189): it fills the <c>{{issue.*}}</c> tokens from a tracker intent's data and
/// the <c>{{input.*}}</c> tokens from operator input, in one pass, and never throws — a missing or unknown token
/// becomes the empty string and is reported so the surface can warn.
/// </summary>
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

    [Fact]
    public void Resolve_FillsEveryIssuePlaceholder_FromTheIntentData()
    {
        const string body = "{{issue.tracker}} {{issue.id}}: {{issue.title}}\n{{issue.description}}\n{{issue.url}}";

        var result = AutopilotTemplateResolver.Resolve(body, _IntentData());

        Assert.Equal("youtrack AC-189: Autopilot templates\nBuild the template foundation.\nhttps://youtrack/AC-189", result.Text);
        Assert.Empty(result.MissingPlaceholders);
    }

    [Fact]
    public void Resolve_FillsInputPlaceholders_FromOperatorInput()
    {
        var input = new Dictionary<string, string> { ["branch"] = "feat/AC-189", ["reviewer"] = "Zyra" };

        var result = AutopilotTemplateResolver.Resolve("Work on {{input.branch}}, ask {{input.reviewer}}.", intentData: null, input: input);

        Assert.Equal("Work on feat/AC-189, ask Zyra.", result.Text);
        Assert.Empty(result.MissingPlaceholders);
    }

    [Fact]
    public void Resolve_ToleratesWhitespaceInsideTheBraces()
    {
        var result = AutopilotTemplateResolver.Resolve("{{ issue.id }} / {{  input.branch  }}", _IntentData(), new Dictionary<string, string> { ["branch"] = "b" });

        Assert.Equal("AC-189 / b", result.Text);
        Assert.Empty(result.MissingPlaceholders);
    }

    [Fact]
    public void Resolve_MissingAndUnknownTokens_BecomeEmptyAndAreReported_WithoutThrowing()
    {
        // issue.url is absent from the data, input.branch has no input, and foo.bar is not a token the resolver knows.
        var data = new Dictionary<string, string> { ["issue"] = "AC-189" };
        var body = "{{issue.id}}|{{issue.url}}|{{input.branch}}|{{foo.bar}}";

        var act = () => AutopilotTemplateResolver.Resolve(body, data, input: null);

        var result = act();
        Assert.Equal("AC-189|||", result.Text);
        Assert.True(SequenceAssert.ContainsInOrder(result.MissingPlaceholders, "issue.url", "input.branch", "foo.bar"));
    }

    [Fact]
    public void Resolve_ReportsEachMissingNameOnce_InFirstSeenOrder()
    {
        var result = AutopilotTemplateResolver.Resolve("{{input.x}} {{input.y}} {{input.x}}", intentData: null, input: null);

        Assert.True(SequenceAssert.ContainsInOrder(result.MissingPlaceholders, "input.x", "input.y"));
        Assert.Equal(2, System.Linq.Enumerable.Count(result.MissingPlaceholders));
    }

    [Fact]
    public void Resolve_PresentButEmptyIssueField_CountsAsResolved_NotMissing()
    {
        // A blank description is a value the intent carried, not an absent one — the key was there.
        var data = new Dictionary<string, string> { ["description"] = string.Empty };

        var result = AutopilotTemplateResolver.Resolve("[{{issue.description}}]", data);

        Assert.Equal("[]", result.Text);
        Assert.Empty(result.MissingPlaceholders);
    }

    [Fact]
    public void Resolve_OnlyRewritesTheBody_LeavingNonTokenTextIntact()
    {
        // Text that is not a {{token}} — including a lone brace or a C#-style interpolation — passes through untouched.
        const string body = "Ship it. Cost {price} and {{issue.id}} only.";

        var result = AutopilotTemplateResolver.Resolve(body, new Dictionary<string, string> { ["issue"] = "AC-189" });

        Assert.Equal("Ship it. Cost {price} and AC-189 only.", result.Text);
    }
}
