using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The run-picker origin suffix (AC-189, slice 3): two trackers both register "Bug fix" and "Feature", so the picker
/// appends where each came from — a plugin's readable name for a Plugin template, "Yours" for the operator's own,
/// "Built-in" for the shipped ones — so duplicate names are told apart. Pure helper, tested without a host or UI.
/// </summary>
public class AutopilotTemplateOptionLabelTests
{
    // A stand-in plugin-name lookup: maps two owner ids to readable names, everything else unknown (null).
    private static string? PluginName(string id) => id switch
    {
        "youtrack" => "YouTrack",
        "github-issues" => "GitHub Issues",
        _ => null,
    };

    [Fact]
    public void PluginTemplate_UsesThePluginName_NotTheBareId()
    {
        var youtrack = AutopilotTemplate.ForPlugin("youtrack", new("t1", "Feature", "body"));
        var github = AutopilotTemplate.ForPlugin("github-issues", new("t2", "Feature", "body"));

        AutopilotTemplateOptionLabel.For(youtrack, PluginName).Should().Be("Feature · YouTrack");
        AutopilotTemplateOptionLabel.For(github, PluginName).Should().Be("Feature · GitHub Issues");
    }

    [Fact]
    public void PluginTemplate_FallsBackToTheOwnerId_WhenTheNameIsUnknown()
    {
        var template = AutopilotTemplate.ForPlugin("some.unknown.plugin", new("t", "Bug fix", "body"));

        AutopilotTemplateOptionLabel.For(template, PluginName).Should().Be("Bug fix · some.unknown.plugin");
    }

    [Fact]
    public void UserTemplate_IsLabelledYours()
    {
        var template = AutopilotTemplate.ForUser("u", "Bug fix", "body");

        AutopilotTemplateOptionLabel.OriginLabel(template, PluginName).Should().Be("Yours");
        AutopilotTemplateOptionLabel.For(template, PluginName).Should().Be("Bug fix · Yours");
    }

    [Fact]
    public void BuiltinTemplate_IsLabelledBuiltIn()
    {
        var template = new AutopilotTemplate(
            "b", "Bug fix", "body", AutopilotTemplateOrigin.Builtin, OwnerPluginId: null, Editable: true, Deletable: false);

        AutopilotTemplateOptionLabel.OriginLabel(template, PluginName).Should().Be("Built-in");
        AutopilotTemplateOptionLabel.For(template, PluginName).Should().Be("Bug fix · Built-in");
    }
}
