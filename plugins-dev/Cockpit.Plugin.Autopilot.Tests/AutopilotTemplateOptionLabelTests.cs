namespace Cockpit.Plugin.Autopilot.Tests;

// The run-picker origin suffix (AC-189, slice 3): two trackers both register "Bug fix" and "Feature", so the picker
// appends where each came from — a plugin's readable name for a Plugin template, "Yours" for the operator's own,
// "Built-in" for the shipped ones — so duplicate names are told apart. Pure helper, tested without a host or UI.
public class AutopilotTemplateOptionLabelTests
{
    // A stand-in plugin-name lookup: maps two owner ids to readable names, everything else unknown (null).
    private static string? PluginName(string id) => id switch
    {
        "youtrack" => "YouTrack",
        "github-issues" => "GitHub Issues",
        _ => null,
    };

    [Theory]
    [InlineData("youtrack", "Feature", "Feature · YouTrack")]
    [InlineData("github-issues", "Feature", "Feature · GitHub Issues")]
    // An owner the host does not know a readable name for keeps its bare id, so the suffix is never empty.
    [InlineData("some.unknown.plugin", "Bug fix", "Bug fix · some.unknown.plugin")]
    public void PluginTemplate_IsSuffixedWithThePluginName_FallingBackToTheOwnerId(string ownerId, string name, string expected)
    {
        var template = AutopilotTemplate.ForPlugin(ownerId, new("t1", name, "body"));

        Assert.Equal(expected, AutopilotTemplateOptionLabel.For(template, PluginName));
    }

    [Fact]
    public void ATemplateThatCameFromNoPlugin_CarriesItsOwnOriginWord()
    {
        var mine = AutopilotTemplate.ForUser("u", "Bug fix", "body");
        var builtin = new AutopilotTemplate(
            "b", "Bug fix", "body", AutopilotTemplateOrigin.Builtin, OwnerPluginId: null, Editable: true, Deletable: false);

        Assert.Equal("Yours", AutopilotTemplateOptionLabel.OriginLabel(mine, PluginName));
        Assert.Equal("Bug fix · Yours", AutopilotTemplateOptionLabel.For(mine, PluginName));
        Assert.Equal("Built-in", AutopilotTemplateOptionLabel.OriginLabel(builtin, PluginName));
        Assert.Equal("Bug fix · Built-in", AutopilotTemplateOptionLabel.For(builtin, PluginName));
    }
}
